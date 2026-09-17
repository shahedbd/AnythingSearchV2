using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Query execution for <see cref="SearchManager"/>.
///
/// Source preference, fastest first:
/// 1. The in-memory index (<see cref="Search.Memory.MemorySearchService"/>) - a vectorised scan of
///    the packed name blob, typically a few milliseconds regardless of index size.
/// 2. SQLite - used while the snapshot is still loading, and if it ever fails to load.
///
/// There is no third source: Windows Search used to sit behind these as a fallback, but the app
/// no longer depends on it. Before the first indexing phase publishes there is simply nothing to
/// search, and the UI says so (see <see cref="SearchManager.IsSearchLocked"/>).
///
/// IMPORTANT: Microsoft.Data.Sqlite does not implement real asynchronous I/O - its *Async methods
/// run synchronously on the calling thread. Awaiting them straight from the UI thread froze the
/// search box while the query ran. Those queries therefore run on a thread-pool thread so the
/// message pump (and typing) stays live. The in-memory search is fast enough that only its
/// parallel scan leaves the caller's thread, which is why it can answer inside a single keystroke.
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

        // Nothing has been published yet - the UI keeps the search box disabled in this state,
        // so this is only a guard against a query that was already in flight.
        if (IsSearchLocked)
            return (new List<FileEntry>(), 0, SearchSource.None);

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
            StatusChanged?.Invoke($"Search failed: {ex.Message}");

            // If SQLite keeps failing (e.g. a corrupted database), stop retrying it on every
            // keystroke and tell the user the index needs rebuilding.
            if (_consecutiveSqliteFailures >= MaxConsecutiveSqliteFailures)
            {
                _useSqlite = false;
                SearchSourceChanged?.Invoke(CurrentSource);
                StatusChanged?.Invoke("The local database keeps failing - rebuild the index to restore search.");
            }

            return (new List<FileEntry>(), 0, SearchSource.None);
        }
    }

    /// <summary>
    /// Run the query against the in-memory snapshot. The scan itself already spreads across cores,
    /// so this only hops off the caller's thread to keep the UI free while it runs.
    ///
    /// The token is passed to the search but deliberately NOT to Task.Run: that overload raises a
    /// TaskCanceledException when the token is already set at scheduling time, which on a search
    /// box is simply what happens whenever someone types quickly. The scan checks the token and
    /// returns early instead, so a superseded search on this path costs no exception at all.
    /// </summary>
    private Task<(List<FileEntry> Results, int Total)> RunMemorySearchAsync(
        string query, int maxResults, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            if (!_memorySearch.TrySearch(query, maxResults, cancellationToken, out var results, out var total))
                throw new InvalidOperationException("The in-memory index is not loaded.");

            return (results, total);
        });

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
}
