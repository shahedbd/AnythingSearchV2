using AnythingSearch.Models;
using Microsoft.Data.Sqlite;

namespace AnythingSearch.Database;

/// <summary>
/// The SQLite search queries.
///
/// This is the fallback search path: it answers while the in-memory snapshot is still loading,
/// and if that snapshot is ever unavailable. MemorySearchService answers searches the rest of
/// the time, which is why these queries are written for correctness and predictable query plans
/// rather than for per-keystroke latency.
///
/// Kept in its own partial so FileDatabase.cs stays focused on the schema and the write paths.
/// </summary>
public partial class FileDatabase
{
    /// <summary>
    /// Fast search with JOIN - improved relevance sorting like Everything
    /// </summary>
    public async Task<List<FileEntry>> SearchAsync(string query, int limit = 1000, CancellationToken cancellationToken = default)
    {
        using var dbLock = await LockAsync();
        var results = new List<FileEntry>();

        // Escape special SQL LIKE characters - the ESCAPE '\' clause below tells SQLite
        // to use \ as escape character
        var escapedQuery = EscapeLike(query);

        // Search with relevance scoring:
        // 1. Exact name match (highest priority)
        // 2. Name starts with query
        // 3. Name contains query
        // 4. Path contains query (for finding files in matching folders)
        // Order by: folders first, then by relevance, then alphabetically
        // Written as two scans joined by UNION ALL rather than one OR across a join. The OR form
        // made SQLite drive the query from the Folders table ("SCAN fo" plus an index probe into
        // Files for every folder), so it walked the whole dataset and then sorted every match in a
        // temp B-tree. Here Files is scanned once for name matches, folders are matched
        // separately, and Folders is only joined for the rows that survived.
        //
        // This is the fallback path: it runs while the in-memory index is still loading, and if
        // that index is unavailable. MemorySearchService answers searches once it is ready.
        var sql = @"
            SELECT m.Name, fo.Path || '\' || m.Name AS FullPath, m.Ext, m.Size, m.Modified, m.IsFolder
            FROM (
                SELECT Name, FolderId, Ext, Size, Modified, IsFolder,
                    CASE
                        WHEN Name = @exact THEN 1
                        WHEN Name LIKE @startsWith ESCAPE '\' THEN 2
                        ELSE 3
                    END AS Relevance
                FROM Files
                WHERE Name LIKE @contains ESCAPE '\'

                UNION ALL

                SELECT Name, FolderId, Ext, Size, Modified, IsFolder, 4 AS Relevance
                FROM Files
                WHERE FolderId IN (SELECT Id FROM Folders WHERE Path LIKE @contains ESCAPE '\')
                  AND Name NOT LIKE @contains ESCAPE '\'
            ) m
            INNER JOIN Folders fo ON m.FolderId = fo.Id
            ORDER BY
                m.IsFolder DESC,
                m.Relevance ASC,
                LENGTH(m.Name) ASC,
                m.Name ASC
            LIMIT @limit
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@exact", query);
        cmd.Parameters.AddWithValue("@startsWith", $"{escapedQuery}%");
        cmd.Parameters.AddWithValue("@contains", $"%{escapedQuery}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // A superseded search (user kept typing) must stop reading rows immediately
            cancellationToken.ThrowIfCancellationRequested();

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
    public async Task<List<FileEntry>> SearchAdvancedAsync(string query, int limit = 1000, CancellationToken cancellationToken = default)
    {
        using var dbLock = await LockAsync();
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
            // Escape special SQL LIKE characters using backslash
            var term = EscapeLike(terms[i]);
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

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // A superseded search (user kept typing) must stop reading rows immediately
            cancellationToken.ThrowIfCancellationRequested();

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
}
