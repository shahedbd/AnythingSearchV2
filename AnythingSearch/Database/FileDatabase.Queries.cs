using Microsoft.Data.Sqlite;

namespace AnythingSearch.Database;

/// <summary>
/// Read/maintenance queries used by the index catch-up pass
/// (see BackgroundIndexingService.CatchUp.cs). Kept in its own partial so FileDatabase.cs
/// stays focused on the indexing and search paths.
/// </summary>
public partial class FileDatabase
{
    /// <summary>
    /// Every indexed folder with the LastWriteTime it had when it was indexed, keyed by full path.
    /// A directory whose stored timestamp differs from disk has gained or lost entries.
    /// </summary>
    public async Task<Dictionary<string, long>> GetFolderModifiedMapAsync(CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var sql = @"
            SELECT fo.Path || '\' || f.Name AS FullPath, f.Modified
            FROM Files f
            INNER JOIN Folders fo ON f.FolderId = fo.Id
            WHERE f.IsFolder = 1
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            map[reader.GetString(0)] = reader.GetInt64(1);
        }

        return map;
    }

    /// <summary>
    /// Names of everything indexed directly inside <paramref name="folderPath"/> (files and folders).
    /// </summary>
    public async Task<HashSet<string>> GetChildNamesAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sql = @"
            SELECT f.Name
            FROM Files f
            INNER JOIN Folders fo ON f.FolderId = fo.Id
            WHERE fo.Path = @folder COLLATE NOCASE
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@folder", folderPath);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// Refresh a folder row's stored LastWriteTime so the next catch-up pass can skip it.
    /// Restricted to folder rows so a file with the same name is not touched.
    /// </summary>
    public async Task UpdateFolderModifiedAsync(string folderFullPath, DateTime modified)
    {
        var parentPath = Path.GetDirectoryName(folderFullPath) ?? "";
        var name = Path.GetFileName(folderFullPath);
        if (string.IsNullOrEmpty(name)) return;

        var sql = @"
            UPDATE Files
            SET Modified = @m
            WHERE Name = @n AND IsFolder = 1
            AND FolderId IN (SELECT Id FROM Folders WHERE Path = @folder COLLATE NOCASE)
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@folder", parentPath);
        cmd.Parameters.AddWithValue("@m", modified.Ticks);
        await cmd.ExecuteNonQueryAsync();
    }
}
