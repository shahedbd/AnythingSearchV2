using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Query execution for <see cref="SearchManager"/>.
///
/// IMPORTANT: neither Microsoft.Data.Sqlite nor the OLE DB provider used for Windows Search
/// implement real asynchronous I/O - their *Async methods run synchronously on the calling
/// thread. Awaiting them straight from the UI thread froze the search box while the query ran.
/// Every query therefore runs on a thread-pool thread so the message pump (and typing) stays live.
/// </summary>
public partial class SearchManager
{
    // The SQLite connection is shared, so only one search query may use it at a time.
    private readonly SemaphoreSlim _searchGate = new(1, 1);

    /// <summary>
    /// Perform a search using the best available source.
    /// Supports multi-term searches (space-separated words)
    /// </summary>
    public async Task<(List<FileEntry> Results, SearchSource Source)> SearchAsync(
        string query,
        int maxResults = 1000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (new List<FileEntry>(), CurrentSource);

        // Use SQLite if ready, otherwise Windows Search
        if (_useSqlite)
        {
            try
            {
                var results = await RunSqliteSearchAsync(query, maxResults, cancellationToken);
                _consecutiveSqliteFailures = 0;
                return (results, SearchSource.SQLite);
            }
            catch (OperationCanceledException)
            {
                throw; // Superseded by a newer keystroke - not a SQLite failure
            }
            catch (Exception ex)
            {
                _consecutiveSqliteFailures++;
                StatusChanged?.Invoke($"SQLite search failed: {ex.Message}, falling back to Windows Search");

                // If SQLite keeps failing (e.g. a corrupted database), stop retrying it on every
                // keystroke and switch the active source until the index is rebuilt.
                if (_consecutiveSqliteFailures >= MaxConsecutiveSqliteFailures)
                {
                    _useSqlite = false;
                    SearchSourceChanged?.Invoke(CurrentSource);
                    StatusChanged?.Invoke("SQLite search failed repeatedly - switched to Windows Search. Rebuild the index to restore the local database.");
                }

                // Fall back to Windows Search
                if (_windowsSearchAvailable)
                {
                    var fallbackResults = await RunWindowsSearchAsync(query, maxResults, cancellationToken);
                    return (fallbackResults, SearchSource.WindowsSearch);
                }
                return (new List<FileEntry>(), SearchSource.SQLite);
            }
        }
        else if (_windowsSearchAvailable)
        {
            var results = await RunWindowsSearchAsync(query, maxResults, cancellationToken);
            return (results, SearchSource.WindowsSearch);
        }
        else
        {
            // Neither available - try SQLite anyway (might have partial data)
            try
            {
                var results = await RunSqliteSearchAsync(query, maxResults, cancellationToken);
                return (results, SearchSource.SQLite);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return (new List<FileEntry>(), SearchSource.None);
            }
        }
    }

    /// <summary>
    /// Run the SQLite query on a thread-pool thread, serialized on the shared connection.
    /// </summary>
    private async Task<List<FileEntry>> RunSqliteSearchAsync(
        string query, int maxResults, CancellationToken cancellationToken)
    {
        await _searchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Use advanced search for multi-term queries
            return await Task.Run(() => query.Contains(' ')
                ? _database.SearchAdvancedAsync(query, maxResults, cancellationToken)
                : _database.SearchAsync(query, maxResults, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _searchGate.Release();
        }
    }

    /// <summary>
    /// Run the Windows Search (OLE DB) query on a thread-pool thread.
    /// </summary>
    private Task<List<FileEntry>> RunWindowsSearchAsync(
        string query, int maxResults, CancellationToken cancellationToken)
        => Task.Run(() => _windowsSearch.SearchAsync(query, maxResults, cancellationToken), cancellationToken);
}
