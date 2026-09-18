using AnythingSearch.Models;
using Microsoft.Data.Sqlite;

namespace AnythingSearch.Database;

/// <summary>
/// Incremental (single-entry) writes used by FileWatcherService and the index catch-up pass,
/// plus the per-batch transaction helpers. The bulk indexing and search paths stay in
/// FileDatabase.cs; read queries for the catch-up pass live in FileDatabase.Queries.cs.
/// </summary>
public partial class FileDatabase
{
    /// <summary>
    /// Insert a single file entry (for incremental updates from FileWatcher)
    /// Does NOT require BeginBatchAsync - uses its own transaction
    /// </summary>
    public async Task InsertSingleAsync(FileEntry entry)
    {
        using var dbLock = await LockAsync();
        var folderPath = Path.GetDirectoryName(entry.Path) ?? "";
        
        // Ensure folder exists
        var folderId = await GetOrCreateFolderIdAsync(folderPath);

        // OR IGNORE: the UNIQUE index on (FolderId, Name) rejects a row that is already indexed
        // rather than letting a repeated watcher event add a second copy of it.
        var sql = "INSERT OR IGNORE INTO Files (Name, FolderId, Ext, Size, Modified, IsFolder) VALUES (@n, @f, @e, @s, @m, @i)";
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@n", entry.Name);
        cmd.Parameters.AddWithValue("@f", folderId);
        cmd.Parameters.AddWithValue("@e", entry.Extension ?? "");
        cmd.Parameters.AddWithValue("@s", entry.Size);
        cmd.Parameters.AddWithValue("@m", entry.Modified.Ticks);
        cmd.Parameters.AddWithValue("@i", entry.IsFolder ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Get or create folder ID for incremental updates (async version)
    /// </summary>
    private async Task<long> GetOrCreateFolderIdAsync(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
            folderPath = "";

        // Check cache first
        if (_folderCache.TryGetValue(folderPath, out var existingId))
            return existingId;

        // Check database
        var selectSql = "SELECT Id FROM Folders WHERE Path = @path COLLATE NOCASE";
        using (var selectCmd = new SqliteCommand(selectSql, _connection))
        {
            selectCmd.Parameters.AddWithValue("@path", folderPath);
            var result = await selectCmd.ExecuteScalarAsync();
            if (result != null)
            {
                var id = Convert.ToInt64(result);
                _folderCache[folderPath] = id;
                return id;
            }
        }

        // Insert new folder
        var insertSql = "INSERT INTO Folders (Path) VALUES (@path); SELECT last_insert_rowid();";
        using (var insertCmd = new SqliteCommand(insertSql, _connection))
        {
            insertCmd.Parameters.AddWithValue("@path", folderPath);
            var newId = Convert.ToInt64(await insertCmd.ExecuteScalarAsync());
            _folderCache[folderPath] = newId;
            return newId;
        }
    }

    public async Task DeleteByPathAsync(string path)
    {
        using var dbLock = await LockAsync();
        var folderPath = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileName(path);

        // Delete the specific file/folder
        var sql = @"
            DELETE FROM Files 
            WHERE Name = @name 
            AND FolderId IN (SELECT Id FROM Folders WHERE Path = @folder COLLATE NOCASE)
        ";
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@name", fileName);
        cmd.Parameters.AddWithValue("@folder", folderPath);
        await cmd.ExecuteNonQueryAsync();

        // Children only exist if the deleted path was itself an indexed FOLDER, and the
        // overwhelming majority of deletions are files. Establishing that first, with an equality
        // lookup that seeks idx_folders_path, is what keeps the subtree sweep below off the hot
        // path: the file watcher calls this once per deleted entry, and a busy machine deletes
        // thousands of files a minute.
        long folderId;
        using (var lookup = new SqliteCommand(
                   "SELECT Id FROM Folders WHERE Path = @path COLLATE NOCASE", _connection))
        {
            lookup.Parameters.AddWithValue("@path", path);
            var found = await lookup.ExecuteScalarAsync();
            if (found == null || found == DBNull.Value) return;
            folderId = Convert.ToInt64(found);
        }

        // FolderId = @id covers the files sitting directly inside the deleted folder. The prefix
        // below cannot match them - their folder path IS the deleted path, with no trailing
        // separator - so they used to survive the delete as unreachable rows that searches still
        // returned.
        //
        // The prefix MUST be escaped: '_' is a single-character wildcard in LIKE and real paths
        // are full of underscores (C:\Program Files\..., G:\src\MS_Store_App\...), so an
        // unescaped prefix also deleted rows belonging to unrelated sibling folders. But an
        // ESCAPE clause disables SQLite's LIKE-to-range optimisation outright, which made this a
        // full scan of every folder ever indexed - hundreds of thousands of rows - per deleted
        // entry, while holding the connection gate the search box queues on. The explicit range
        // restores the index seek; LIKE stays on as the exact filter over what it returns.
        var childSql = @"
            DELETE FROM Files
            WHERE FolderId = @id
               OR FolderId IN (
                    SELECT Id FROM Folders
                    WHERE Path >= @from AND Path < @to
                      AND Path LIKE @pathPrefix ESCAPE '\')
        ";
        using var childCmd = new SqliteCommand(childSql, _connection);
        childCmd.Parameters.AddWithValue("@id", folderId);
        childCmd.Parameters.AddWithValue("@from", path + "\\");
        // ']' is the next character after '\', so this is the exclusive end of the "path\..." range.
        childCmd.Parameters.AddWithValue("@to", path + "]");
        childCmd.Parameters.AddWithValue("@pathPrefix", EscapeLike(path) + "\\%");
        await childCmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Move/rename an indexed entry. Returns the number of rows updated - 0 means the old path
    /// was never indexed, so the caller should index the new path instead.
    /// </summary>
    public async Task<int> UpdatePathAsync(string oldPath, string newPath)
    {
        using var dbLock = await LockAsync();
        var oldFolderPath = Path.GetDirectoryName(oldPath) ?? "";
        var oldFileName = Path.GetFileName(oldPath);
        var newFolderPath = Path.GetDirectoryName(newPath) ?? "";
        var newFileName = Path.GetFileName(newPath);

        // Get or create new folder
        var newFolderId = await GetOrCreateFolderIdAsync(newFolderPath);

        // OR REPLACE: if the destination name is already indexed the stored row is stale (the
        // rename just overwrote it on disk), so replacing it is correct - and it keeps the rename
        // from failing against the UNIQUE (FolderId, Name) index.
        var sql = @"
            UPDATE OR REPLACE Files
            SET Name = @newName, FolderId = @newFolderId
            WHERE Name = @oldName 
            AND FolderId IN (SELECT Id FROM Folders WHERE Path = @oldFolder COLLATE NOCASE)
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@newName", newFileName);
        cmd.Parameters.AddWithValue("@newFolderId", newFolderId);
        cmd.Parameters.AddWithValue("@oldName", oldFileName);
        cmd.Parameters.AddWithValue("@oldFolder", oldFolderPath);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateFileAsync(FileEntry entry)
    {
        using var dbLock = await LockAsync();
        var folderPath = Path.GetDirectoryName(entry.Path) ?? "";

        var sql = @"
            UPDATE Files 
            SET Ext = @e, Size = @s, Modified = @m 
            WHERE Name = @n 
            AND FolderId IN (SELECT Id FROM Folders WHERE Path = @folder COLLATE NOCASE)
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@n", entry.Name);
        cmd.Parameters.AddWithValue("@folder", folderPath);
        cmd.Parameters.AddWithValue("@e", entry.Extension ?? "");
        cmd.Parameters.AddWithValue("@s", entry.Size);
        cmd.Parameters.AddWithValue("@m", entry.Modified.Ticks);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Escape the LIKE wildcards ('%', '_') and the escape character itself so a literal path can
    /// be used as a LIKE pattern. Must be paired with an ESCAPE '\' clause in the SQL.
    /// </summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<bool> ExistsAsync(string path)
    {
        using var dbLock = await LockAsync();
        var folderPath = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileName(path);

        var sql = @"
            SELECT COUNT(*) FROM Files 
            WHERE Name = @name COLLATE NOCASE
            AND FolderId IN (SELECT Id FROM Folders WHERE Path = @folder COLLATE NOCASE)
        ";
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@name", fileName);
        cmd.Parameters.AddWithValue("@folder", folderPath);
        var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        return count > 0;
    }

    private bool _inIncrementalTransaction = false;

    /// <summary>
    /// Wraps a batch of incremental writes (InsertSingleAsync/DeleteByPathAsync/UpdatePathAsync/UpdateFileAsync)
    /// in a single transaction instead of SQLite's default one-transaction-per-statement autocommit.
    /// Used by FileWatcherService when flushing a batch of debounced file-system changes.
    /// </summary>
    public async Task BeginIncrementalTransactionAsync()
    {
        using var dbLock = await LockAsync();
        if (_inIncrementalTransaction) return;
        await ExecuteNonQueryAsync("BEGIN TRANSACTION");
        _inIncrementalTransaction = true;
    }

    public async Task CommitIncrementalTransactionAsync()
    {
        using var dbLock = await LockAsync();
        if (!_inIncrementalTransaction) return;
        await ExecuteNonQueryAsync("COMMIT");
        _inIncrementalTransaction = false;
    }
}
