using System.Data;
using Microsoft.Data.Sqlite;
using AnythingSearch.Models;
using System.Text;
using System.Collections.Concurrent;

namespace AnythingSearch.Database;

/// <summary>
/// High-performance file database optimized for both speed AND size.
/// 
/// Size optimizations:
/// - Normalized folder paths (stored once, referenced by ID)
/// - Compact integer IDs instead of repeated strings
/// - Checkpoint WAL after indexing to merge files
/// - VACUUM to reclaim space
/// 
/// Speed optimizations:
/// - Prepared statements with parameter reuse
/// - Large batch inserts
/// - Aggressive SQLite PRAGMA settings
/// - Indexes created AFTER bulk insert
/// </summary>
public class FileDatabase : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private static bool _initialized = false;

    // Thread-safe pending inserts
    private readonly ConcurrentBag<FileEntry> _pendingInserts = new();
    private readonly object _flushLock = new();
    private const int BulkInsertThreshold = 10000;

    // Folder path cache (path -> id) for normalized storage
    private readonly ConcurrentDictionary<string, long> _folderCache = new(StringComparer.OrdinalIgnoreCase);
    private long _nextFolderId = 1;
    private readonly object _folderLock = new();

    // Prepared statements
    private SqliteCommand? _insertFileCommand;
    private SqliteCommand? _insertFolderCommand;
    private bool _inTransaction = false;

    public FileDatabase()
    {
        if (!_initialized)
        {
            SQLitePCL.Batteries.Init();
            _initialized = true;
        }

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnythingSearch");

        Directory.CreateDirectory(appData);
        _dbPath = Path.Combine(appData, "AnythingSearch.db");
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath};Pooling=True;Cache=Shared");
        await _connection.OpenAsync();

        // Normalized schema: Folders table + Files table
        // This saves ~60-70% space compared to storing full paths
        var createTables = @"
            CREATE TABLE IF NOT EXISTS Folders (
                Id INTEGER PRIMARY KEY,
                Path TEXT NOT NULL COLLATE NOCASE
            );

            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE,
                FolderId INTEGER NOT NULL,
                Ext TEXT COLLATE NOCASE,
                Size INTEGER,
                Modified INTEGER,
                IsFolder INTEGER DEFAULT 0
            );
        ";

        using var cmd = new SqliteCommand(createTables, _connection);
        await cmd.ExecuteNonQueryAsync();

        // MAXIMUM performance settings for bulk insert
        await ExecuteNonQueryAsync("PRAGMA journal_mode = WAL");
        await ExecuteNonQueryAsync("PRAGMA synchronous = OFF");
        await ExecuteNonQueryAsync("PRAGMA cache_size = -256000");
        await ExecuteNonQueryAsync("PRAGMA temp_store = MEMORY");
        await ExecuteNonQueryAsync("PRAGMA mmap_size = 536870912");
        await ExecuteNonQueryAsync("PRAGMA page_size = 4096");
        await ExecuteNonQueryAsync("PRAGMA locking_mode = EXCLUSIVE");
        await ExecuteNonQueryAsync("PRAGMA auto_vacuum = NONE");
    }

    public async Task ClearAsync()
    {
        // Clear caches
        while (_pendingInserts.TryTake(out _)) { }
        _folderCache.Clear();
        _nextFolderId = 1;

        // Drop and recreate for fastest clear
        await ExecuteNonQueryAsync("DROP TABLE IF EXISTS Files");
        await ExecuteNonQueryAsync("DROP TABLE IF EXISTS Folders");

        var createTables = @"
            CREATE TABLE Folders (
                Id INTEGER PRIMARY KEY,
                Path TEXT NOT NULL COLLATE NOCASE
            );

            CREATE TABLE Files (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE,
                FolderId INTEGER NOT NULL,
                Ext TEXT COLLATE NOCASE,
                Size INTEGER,
                Modified INTEGER,
                IsFolder INTEGER DEFAULT 0
            );
        ";

        using var cmd = new SqliteCommand(createTables, _connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task BeginBatchAsync()
    {
        await ExecuteNonQueryAsync("BEGIN TRANSACTION");
        _inTransaction = true;

        // Prepare insert statements for reuse
        _insertFolderCommand = new SqliteCommand(
            "INSERT INTO Folders (Id, Path) VALUES (@id, @path)",
            _connection);
        _insertFolderCommand.Parameters.Add("@id", SqliteType.Integer);
        _insertFolderCommand.Parameters.Add("@path", SqliteType.Text);
        _insertFolderCommand.Prepare();

        _insertFileCommand = new SqliteCommand(
            "INSERT INTO Files (Name, FolderId, Ext, Size, Modified, IsFolder) VALUES (@n, @f, @e, @s, @m, @i)",
            _connection);
        _insertFileCommand.Parameters.Add("@n", SqliteType.Text);
        _insertFileCommand.Parameters.Add("@f", SqliteType.Integer);
        _insertFileCommand.Parameters.Add("@e", SqliteType.Text);
        _insertFileCommand.Parameters.Add("@s", SqliteType.Integer);
        _insertFileCommand.Parameters.Add("@m", SqliteType.Integer);
        _insertFileCommand.Parameters.Add("@i", SqliteType.Integer);
        _insertFileCommand.Prepare();
    }

    public async Task CommitBatchAsync()
    {
        await FlushPendingInsertsAsync();

        if (_inTransaction)
        {
            await ExecuteNonQueryAsync("COMMIT");
            _inTransaction = false;
        }

        _insertFileCommand?.Dispose();
        _insertFolderCommand?.Dispose();
        _insertFileCommand = null;
        _insertFolderCommand = null;
    }

    /// <summary>
    /// Create indexes and optimize database size AFTER bulk insert
    /// </summary>
    public async Task FinalizeIndexingAsync()
    {
        // Create indexes (much faster after all data is inserted)
        await ExecuteNonQueryAsync("CREATE INDEX IF NOT EXISTS idx_files_name ON Files(Name)");
        await ExecuteNonQueryAsync("CREATE INDEX IF NOT EXISTS idx_files_folder ON Files(FolderId)");
        await ExecuteNonQueryAsync("CREATE INDEX IF NOT EXISTS idx_files_ext ON Files(Ext)");
        await ExecuteNonQueryAsync("CREATE INDEX IF NOT EXISTS idx_folders_path ON Folders(Path)");

        // Checkpoint WAL to merge into main database file
        await ExecuteNonQueryAsync("PRAGMA wal_checkpoint(TRUNCATE)");

        // Switch to normal mode
        await ExecuteNonQueryAsync("PRAGMA synchronous = NORMAL");
        await ExecuteNonQueryAsync("PRAGMA locking_mode = NORMAL");

        // Optimize
        await ExecuteNonQueryAsync("ANALYZE");

        // VACUUM to reclaim space and compact the database
        // This can reduce size by 20-30%
        await ExecuteNonQueryAsync("VACUUM");
    }

    /// <summary>
    /// Thread-safe insert - adds to pending batch
    /// </summary>
    public Task InsertAsync(FileEntry entry)
    {
        _pendingInserts.Add(entry);

        if (_pendingInserts.Count >= BulkInsertThreshold)
        {
            return FlushPendingInsertsAsync();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get or create folder ID (thread-safe)
    /// </summary>
    private long GetOrCreateFolderId(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
            folderPath = "";

        if (_folderCache.TryGetValue(folderPath, out var existingId))
            return existingId;

        lock (_folderLock)
        {
            // Double-check after acquiring lock
            if (_folderCache.TryGetValue(folderPath, out existingId))
                return existingId;

            var newId = _nextFolderId++;
            _folderCache[folderPath] = newId;

            // Insert folder into database
            if (_insertFolderCommand != null)
            {
                _insertFolderCommand.Parameters["@id"].Value = newId;
                _insertFolderCommand.Parameters["@path"].Value = folderPath;
                _insertFolderCommand.ExecuteNonQuery();
            }

            return newId;
        }
    }

    /// <summary>
    /// Flush all pending inserts
    /// </summary>
    private async Task FlushPendingInsertsAsync()
    {
        if (!Monitor.TryEnter(_flushLock))
            return;

        try
        {
            var items = new List<FileEntry>(BulkInsertThreshold + 1000);
            while (_pendingInserts.TryTake(out var entry))
            {
                items.Add(entry);
            }

            if (items.Count == 0) return;

            if (_insertFileCommand != null)
            {
                foreach (var entry in items)
                {
                    // Get folder path and ID
                    var folderPath = entry.IsFolder
                        ? Path.GetDirectoryName(entry.Path) ?? ""
                        : Path.GetDirectoryName(entry.Path) ?? "";

                    var folderId = GetOrCreateFolderId(folderPath);

                    _insertFileCommand.Parameters["@n"].Value = entry.Name;
                    _insertFileCommand.Parameters["@f"].Value = folderId;
                    _insertFileCommand.Parameters["@e"].Value = entry.Extension ?? "";
                    _insertFileCommand.Parameters["@s"].Value = entry.Size;
                    _insertFileCommand.Parameters["@m"].Value = entry.Modified.Ticks;
                    _insertFileCommand.Parameters["@i"].Value = entry.IsFolder ? 1 : 0;

                    _insertFileCommand.ExecuteNonQuery();
                }
            }
        }
        finally
        {
            Monitor.Exit(_flushLock);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Fast search with JOIN - improved relevance sorting like Everything
    /// </summary>
    public async Task<List<FileEntry>> SearchAsync(string query, int limit = 1000)
    {
        var results = new List<FileEntry>();

        // Escape special SQL LIKE characters
        var escapedQuery = query.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

        // Search with relevance scoring:
        // 1. Exact name match (highest priority)
        // 2. Name starts with query
        // 3. Name contains query
        // 4. Path contains query (for finding files in matching folders)
        // Order by: folders first, then by relevance, then alphabetically
        var sql = @"
            SELECT f.Name, fo.Path || '\' || f.Name AS FullPath, f.Ext, f.Size, f.Modified, f.IsFolder,
                CASE 
                    WHEN f.Name = @exact THEN 1
                    WHEN f.Name LIKE @startsWith ESCAPE '\' THEN 2
                    WHEN f.Name LIKE @contains ESCAPE '\' THEN 3
                    WHEN fo.Path LIKE @contains ESCAPE '\' THEN 4
                    ELSE 5
                END AS Relevance
            FROM Files f
            INNER JOIN Folders fo ON f.FolderId = fo.Id
            WHERE f.Name LIKE @contains ESCAPE '\' 
               OR fo.Path LIKE @contains ESCAPE '\'
            ORDER BY 
                f.IsFolder DESC,
                Relevance ASC,
                LENGTH(f.Name) ASC,
                f.Name ASC
            LIMIT @limit
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@exact", query);
        cmd.Parameters.AddWithValue("@startsWith", $"{escapedQuery}%");
        cmd.Parameters.AddWithValue("@contains", $"%{escapedQuery}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new FileEntry
            {
                Name = reader.GetString(0),
                Path = reader.GetString(1),
                Extension = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Size = reader.GetInt64(3),
                Modified = new DateTime(reader.GetInt64(4)),
                IsFolder = reader.GetInt32(5) == 1
            });
        }

        return results;
    }

    /// <summary>
    /// Advanced search with multiple terms (space-separated)
    /// Each term must match somewhere in name or path
    /// </summary>
    public async Task<List<FileEntry>> SearchAdvancedAsync(string query, int limit = 1000)
    {
        var results = new List<FileEntry>();

        // Split query into terms
        var terms = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        if (terms.Length == 0)
            return results;

        // Build WHERE clause for all terms (AND logic)
        var whereConditions = new List<string>();
        var parameters = new Dictionary<string, string>();

        for (int i = 0; i < terms.Length; i++)
        {
            var term = terms[i].Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
            var paramName = $"@t{i}";
            parameters[paramName] = $"%{term}%";
            whereConditions.Add($"(f.Name LIKE {paramName} ESCAPE '\\' OR fo.Path LIKE {paramName} ESCAPE '\\')");
        }

        var sql = $@"
            SELECT f.Name, fo.Path || '\' || f.Name AS FullPath, f.Ext, f.Size, f.Modified, f.IsFolder
            FROM Files f
            INNER JOIN Folders fo ON f.FolderId = fo.Id
            WHERE {string.Join(" AND ", whereConditions)}
            ORDER BY 
                f.IsFolder DESC,
                LENGTH(f.Name) ASC,
                f.Name ASC
            LIMIT @limit
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        foreach (var param in parameters)
        {
            cmd.Parameters.AddWithValue(param.Key, param.Value);
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new FileEntry
            {
                Name = reader.GetString(0),
                Path = reader.GetString(1),
                Extension = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Size = reader.GetInt64(3),
                Modified = new DateTime(reader.GetInt64(4)),
                IsFolder = reader.GetInt32(5) == 1
            });
        }

        return results;
    }

    public async Task<long> GetCountAsync()
    {
        using var cmd = new SqliteCommand("SELECT COUNT(*) FROM Files", _connection);
        var result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToInt64(result) : 0;
    }

    /// <summary>
    /// Insert a single file entry (for incremental updates from FileWatcher)
    /// Does NOT require BeginBatchAsync - uses its own transaction
    /// </summary>
    public async Task InsertSingleAsync(FileEntry entry)
    {
        var folderPath = Path.GetDirectoryName(entry.Path) ?? "";

        // Ensure folder exists
        var folderId = await GetOrCreateFolderIdAsync(folderPath);

        var sql = "INSERT INTO Files (Name, FolderId, Ext, Size, Modified, IsFolder) VALUES (@n, @f, @e, @s, @m, @i)";
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

        // Also delete children if it was a folder
        var childSql = @"
            DELETE FROM Files 
            WHERE FolderId IN (SELECT Id FROM Folders WHERE Path LIKE @pathPrefix COLLATE NOCASE)
        ";
        using var childCmd = new SqliteCommand(childSql, _connection);
        childCmd.Parameters.AddWithValue("@pathPrefix", path + "\\%");
        await childCmd.ExecuteNonQueryAsync();
    }

    public async Task UpdatePathAsync(string oldPath, string newPath)
    {
        var oldFolderPath = Path.GetDirectoryName(oldPath) ?? "";
        var oldFileName = Path.GetFileName(oldPath);
        var newFolderPath = Path.GetDirectoryName(newPath) ?? "";
        var newFileName = Path.GetFileName(newPath);

        // Get or create new folder
        var newFolderId = await GetOrCreateFolderIdAsync(newFolderPath);

        var sql = @"
            UPDATE Files 
            SET Name = @newName, FolderId = @newFolderId
            WHERE Name = @oldName 
            AND FolderId IN (SELECT Id FROM Folders WHERE Path = @oldFolder COLLATE NOCASE)
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@newName", newFileName);
        cmd.Parameters.AddWithValue("@newFolderId", newFolderId);
        cmd.Parameters.AddWithValue("@oldName", oldFileName);
        cmd.Parameters.AddWithValue("@oldFolder", oldFolderPath);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateFileAsync(FileEntry entry)
    {
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

    public async Task<bool> ExistsAsync(string path)
    {
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

    private async Task ExecuteNonQueryAsync(string sql)
    {
        using var cmd = new SqliteCommand(sql, _connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _insertFileCommand?.Dispose();
        _insertFolderCommand?.Dispose();
        _connection?.Dispose();
    }
}