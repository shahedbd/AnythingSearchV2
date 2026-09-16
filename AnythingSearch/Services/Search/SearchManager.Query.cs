using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Query execution for <see cref="SearchManager"/>.
///
/// Source preference, fastest first:
/// 1. The in-memory index (<see cref="Search.Memory.MemorySearchService"/>) - a vectorised scan of
///    the packed name blob, typically a few milliseconds regardless of index size.
/// 2. SQLite - used while the snapshot is still loading, and if it ever fails to load.
/// 3. Windows Search - used before the local database exists, or if SQLite keeps failing.
///
/// IMPORTANT: neither Microsoft.Data.Sqlite nor the OLE DB provider used for Windows Search
/// implement real asynchronous I/O - their *Async methods run synchronously on the calling
/// thread. Awaiting them straight from the UI thread froze the search box while the query ran.
/// Those queries therefore run on a thread-pool thread so the message pump (and typing) stays
/// live. The in-memory search is fast enough that only its parallel scan leaves the caller's
/// thread, which is why it can answer inside a single keystroke.
/// </summary>
public partial class SearchManager
{
    // The SQLite connection is shared, so only one search query may use it at a time.
    // The in-memory index needs no such gate: its snapshot is immutable.
    private readonly SemaphoreSlim _searchGate = new(1, 1);

    /// <summary>
    /// Perform a search using the best available source.
    /// Supports multi-term searches (space-separated words)
    /// </summary>
    /// <returns>
    /// The page of results, how many entries matched in total (which is usually far more than
    /// the page holds), and which source answered. The total is returned rather than stored on
    /// the manager because searches overlap: a superseded query finishing late would otherwise
    /// overwrite the count belonging to the one actually on screen.
    /// </returns>
    public async Task<(List<FileEntry> Results, int TotalMatches, SearchSource Source)> SearchAsync(
        string query,
        int maxResults = 1000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (new List<FileEntry>(), 0, CurrentSource);

        // Fastest path: everything is already in RAM.
        if (_memorySearch.IsReady)
        {
            try
            {
                var (results, total) = await RunMemorySearchAsync(query, maxResults, cancellationToken);
                return (results, total, SearchSource.Memory);
            }
            catch (OperationCanceledException)
            {
                throw; // Superseded by a newer keystroke
            }
            catch (Exception ex)
            {
                // Never let a snapshot problem break search - drop to the database below.
                StatusChanged?.Invoke($"In-memory search failed: {ex.Message} - using the database");
                _memorySearch.Invalidate();
            }
        }

        // Use SQLite if ready, otherwise Windows Search
        if (_useSqlite)
        {
            try
            {
                var results = await RunSqliteSearchAsync(query, maxResults, cancellationToken);
                _consecutiveSqliteFailures = 0;
                return (results, results.Count, SearchSource.SQLite);
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
                    return (fallbackResults, fallbackResults.Count, SearchSource.WindowsSearch);
                }

                return (new List<FileEntry>(), 0, SearchSource.SQLite);
            }
        }
        else if (_windowsSearchAvailable)
        {
            var results = await RunWindowsSearchAsync(query, maxResults, cancellationToken);
            return (results, results.Count, SearchSource.WindowsSearch);
        }
        else
        {
            // Neither available - try SQLite anyway (might have partial data)
            try
            {
                var results = await RunSqliteSearchAsync(query, maxResults, cancellationToken);
                return (results, results.Count, SearchSource.SQLite);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return (new List<FileEntry>(), 0, SearchSource.None);
            }
        }
    }

    /// <summary>
    /// Run the query against the in-memory snapshot. The scan itself already spreads across cores,
    /// so this only hops off the caller's thread to keep the UI free while it runs.
    /// </summary>
    private Task<(List<FileEntry> Results, int Total)> RunMemorySearchAsync(
        string query, int maxResults, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            if (!_memorySearch.TrySearch(query, maxResults, cancellationToken, out var results, out var total))
                throw new InvalidOperationException("The in-memory index is not loaded.");

            return (results, total);
        }, cancellationToken);

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
