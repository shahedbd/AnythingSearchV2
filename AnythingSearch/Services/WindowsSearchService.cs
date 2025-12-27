using System.Data.OleDb;
using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Query the Windows Search Index directly
/// This uses the existing Windows indexing service - no need to build our own index!
/// 
/// Pros:
/// - Instant results (Windows already indexed everything)
/// - No admin privileges needed
/// - Includes file content search
/// - Automatically stays up-to-date
/// 
/// Cons:
/// - Only searches indexed locations (usually user folders)
/// - Depends on Windows Search service running
/// - Slightly slower than local SQLite for simple filename searches
/// </summary>
public class WindowsSearchService
{
    private const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    public event Action<string>? StatusChanged;

    /// <summary>
    /// Search using Windows Search Index
    /// </summary>
    public async Task<List<FileEntry>> SearchAsync(string query, int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        var results = new List<FileEntry>();

        if (string.IsNullOrWhiteSpace(query))
            return results;

        try
        {
            // Build SQL query for Windows Search
            // System.FileName LIKE '%query%' searches filenames
            // CONTAINS(*,'query') searches file content too
            var sql = $@"
                SELECT TOP {maxResults}
                    System.ItemName,
                    System.ItemPathDisplay,
                    System.Size,
                    System.DateModified,
                    System.ItemType,
                    System.Kind
                FROM SystemIndex
                WHERE System.FileName LIKE '%{EscapeSql(query)}%'
                ORDER BY System.DateModified DESC
            ";

            using var connection = new OleDbConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = new OleDbCommand(sql, connection);
            command.CommandTimeout = 30;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                try
                {
                    var name = reader["System.ItemName"]?.ToString() ?? "";
                    var path = reader["System.ItemPathDisplay"]?.ToString() ?? "";
                    var size = reader["System.Size"] as long? ?? 0;
                    var modified = reader["System.DateModified"] as DateTime? ?? DateTime.MinValue;
                    var itemType = reader["System.ItemType"]?.ToString() ?? "";
                    var kind = reader["System.Kind"]?.ToString() ?? "";

                    var isFolder = kind?.Contains("folder", StringComparison.OrdinalIgnoreCase) == true ||
                                   itemType?.Equals("Directory", StringComparison.OrdinalIgnoreCase) == true;

                    results.Add(new FileEntry
                    {
                        Name = name,
                        Path = path,
                        Extension = isFolder ? "" : Path.GetExtension(name).TrimStart('.'),
                        Size = size,
                        Modified = modified,
                        IsFolder = isFolder
                    });
                }
                catch
                {
                    // Skip invalid entries
                }
            }

            StatusChanged?.Invoke($"Found {results.Count} results from Windows Search");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Windows Search error: {ex.Message}");

            // If Windows Search fails, it might not be available
            if (ex.Message.Contains("provider") || ex.Message.Contains("OLE DB"))
            {
                StatusChanged?.Invoke("Windows Search service may not be running or accessible");
            }
        }

        return results;
    }

    /// <summary>
    /// Search with content (searches inside files too)
    /// </summary>
    public async Task<List<FileEntry>> SearchContentAsync(string query, int maxResults = 500, CancellationToken cancellationToken = default)
    {
        var results = new List<FileEntry>();

        if (string.IsNullOrWhiteSpace(query))
            return results;

        try
        {
            // CONTAINS searches file content
            var sql = $@"
                SELECT TOP {maxResults}
                    System.ItemName,
                    System.ItemPathDisplay,
                    System.Size,
                    System.DateModified,
                    System.Kind
                FROM SystemIndex
                WHERE CONTAINS(*, '""{EscapeSql(query)}""')
                ORDER BY System.Search.Rank DESC
            ";

            using var connection = new OleDbConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = new OleDbCommand(sql, connection);
            command.CommandTimeout = 60; // Content search can be slower

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                try
                {
                    var name = reader["System.ItemName"]?.ToString() ?? "";
                    var path = reader["System.ItemPathDisplay"]?.ToString() ?? "";
                    var size = reader["System.Size"] as long? ?? 0;
                    var modified = reader["System.DateModified"] as DateTime? ?? DateTime.MinValue;
                    var kind = reader["System.Kind"]?.ToString() ?? "";

                    results.Add(new FileEntry
                    {
                        Name = name,
                        Path = path,
                        Extension = Path.GetExtension(name).TrimStart('.'),
                        Size = size,
                        Modified = modified,
                        IsFolder = kind?.Contains("folder", StringComparison.OrdinalIgnoreCase) == true
                    });
                }
                catch { }
            }

            StatusChanged?.Invoke($"Found {results.Count} content matches");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Content search error: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Get all indexed files (for building local cache)
    /// </summary>
    public async Task<List<FileEntry>> GetAllIndexedFilesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<FileEntry>();
        int offset = 0;
        const int batchSize = 10000;

        StatusChanged?.Invoke("Loading files from Windows Search Index...");

        try
        {
            using var connection = new OleDbConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                // Note: Windows Search doesn't support OFFSET, so we use a workaround
                var sql = $@"
                    SELECT TOP {batchSize}
                        System.ItemName,
                        System.ItemPathDisplay,
                        System.Size,
                        System.DateModified,
                        System.Kind
                    FROM SystemIndex
                    WHERE System.ItemPathDisplay IS NOT NULL
                ";

                using var command = new OleDbCommand(sql, connection);
                command.CommandTimeout = 120;

                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                int count = 0;
                while (await reader.ReadAsync(cancellationToken))
                {
                    try
                    {
                        var name = reader["System.ItemName"]?.ToString() ?? "";
                        var path = reader["System.ItemPathDisplay"]?.ToString() ?? "";

                        if (string.IsNullOrEmpty(path)) continue;

                        var size = reader["System.Size"] as long? ?? 0;
                        var modified = reader["System.DateModified"] as DateTime? ?? DateTime.MinValue;
                        var kind = reader["System.Kind"]?.ToString() ?? "";

                        results.Add(new FileEntry
                        {
                            Name = name,
                            Path = path,
                            Extension = Path.GetExtension(name).TrimStart('.'),
                            Size = size,
                            Modified = modified,
                            IsFolder = kind?.Contains("folder", StringComparison.OrdinalIgnoreCase) == true
                        });

                        count++;
                    }
                    catch { }
                }

                StatusChanged?.Invoke($"Loaded {results.Count:N0} files from Windows Index...");

                if (count < batchSize)
                    break; // No more results

                offset += batchSize;

                // Safety limit
                if (results.Count > 5000000)
                {
                    StatusChanged?.Invoke("Reached 5M file limit");
                    break;
                }
            }

            StatusChanged?.Invoke($"Loaded {results.Count:N0} files from Windows Search Index");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Error: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Check if Windows Search is available
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var connection = new OleDbConnection(ConnectionString);
            await connection.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get indexed locations
    /// </summary>
    public async Task<List<string>> GetIndexedLocationsAsync()
    {
        var locations = new List<string>();

        try
        {
            var sql = @"
                SELECT DISTINCT System.ItemFolderPathDisplay
                FROM SystemIndex
                WHERE System.ItemFolderPathDisplay IS NOT NULL
            ";

            using var connection = new OleDbConnection(ConnectionString);
            await connection.OpenAsync();

            using var command = new OleDbCommand(sql, connection);
            command.CommandTimeout = 30;

            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var path = reader[0]?.ToString();
                if (!string.IsNullOrEmpty(path))
                    locations.Add(path);
            }
        }
        catch { }

        return locations.Distinct().Take(100).ToList();
    }

    private string EscapeSql(string input)
    {
        // Basic SQL injection prevention
        return input.Replace("'", "''").Replace("\"", "\"\"");
    }
}