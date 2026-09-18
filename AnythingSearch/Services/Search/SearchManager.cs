using AnythingSearch.Database;
using AnythingSearch.Helper;
using AnythingSearch.Models;
using AnythingSearch.Services.Search.Memory;

namespace AnythingSearch.Services;

/// <summary>
/// Manages search operations on top of the local index.
///
/// Strategy:
/// 1. Searches are answered from the in-memory snapshot whenever one is loaded.
/// 2. SQLite answers them while that snapshot is still being built, or if it fails to load.
/// 3. Until the first indexing phase has published anything there is nothing to search, and
///    <see cref="IsSearchLocked"/> tells the UI to disable the search box and say so.
///
/// Windows Search is deliberately not used. It was previously queried while the local index was
/// being built, which meant two indexes were being consulted, results changed shape halfway
/// through a build, and the app depended on a service the user may have disabled. The phased
/// indexer makes the local index searchable within seconds instead, which removes the need.
///
/// Split into partial classes: this file owns initialization and the rebuild/status API;
/// see SearchManager.Query.cs for search execution and SearchManager.Events.cs for the
/// indexing/status event wiring.
/// </summary>
public partial class SearchManager : IDisposable
{
    private readonly BackgroundIndexingService _indexingService;
    private readonly FileDatabase _database;
    private readonly MemorySearchService _memorySearch;

    private bool _useSqlite = false;
    private bool _disposed = false;

    // Circuit breaker: stop hammering a broken SQLite database on every keystroke
    private int _consecutiveSqliteFailures = 0;
    private const int MaxConsecutiveSqliteFailures = 3;

    /// <summary>
    /// Fired when the search source changes
    /// </summary>
    public event Action<SearchSource>? SearchSourceChanged;

    /// <summary>
    /// Fired when search status updates
    /// </summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// Current search source being used
    /// </summary>
    public SearchSource CurrentSource => _memorySearch.IsReady
        ? SearchSource.Memory
        : _useSqlite ? SearchSource.SQLite : SearchSource.None;

    /// <summary>
    /// The in-memory index that answers searches once it has loaded. Exposed so the file watcher
    /// can keep it in step with the database it writes to.
    /// </summary>
    public MemorySearchService MemoryIndex => _memorySearch;

    /// <summary>
    /// Whether anything is available to search - true once the first indexing phase has published.
    /// </summary>
    public bool IsDatabaseReady => _indexingService.IsDatabaseReady;

    /// <summary>
    /// True while there is nothing to search yet, either on a first run before phase 1 finishes
    /// or during a full rebuild. The UI disables the search box and shows a status instead.
    /// </summary>
    public bool IsSearchLocked => !_indexingService.HasSearchableData;

    /// <summary>
    /// Current indexing progress
    /// </summary>
    public DatabaseStatus IndexingStatus => _indexingService.Status;

    /// <summary>Persisted phase/checkpoint state, for status display.</summary>
    public IndexingState IndexingState => _indexingService.State;

    /// <summary>
    /// Whether indexing is in progress
    /// </summary>
    public bool IsIndexing => _indexingService.IsIndexing;

    public SearchManager(FileDatabase database)
    {
        _database = database;
        _indexingService = new BackgroundIndexingService(database);
        _memorySearch = new MemorySearchService(database.DatabasePath);
        _database.MaintenanceStatusChanged += status => StatusChanged?.Invoke(status);
        _memorySearch.StatusChanged += status => StatusChanged?.Invoke(status);
        _memorySearch.SnapshotReady += () => SearchSourceChanged?.Invoke(SearchSource.Memory);

        // Subscribe to indexing events
        _indexingService.DatabaseReady += OnDatabaseReady;
        _indexingService.ProgressChanged += OnIndexingProgress;
        _indexingService.IndexingFailed += OnIndexingFailed;
        _indexingService.ScopePublished += OnScopePublished;
    }

    /// <summary>
    /// Initialize the search manager: open the index, publish whatever is already there, and let
    /// the indexer resume anything still missing in the background.
    /// </summary>
    public async Task InitializeAsync()
    {
        StatusChanged?.Invoke("Checking local index...");

        // Decides whether the index is complete, partial or missing, and resumes in the
        // background if needed. Never blocks on the disk walk itself.
        await _indexingService.InitializeAsync();

        if (!_indexingService.IsDatabaseReady) return;

        _useSqlite = true;
        SearchSourceChanged?.Invoke(SearchSource.SQLite);
        StatusChanged?.Invoke($"Using local database ({_indexingService.Status.TotalItems:N0} items)");

        // Load the index into RAM in the background. Searches keep using SQLite until the
        // snapshot lands, then switch over automatically.
        _memorySearch.Start();

        // Databases written before (FolderId, Name) became unique can hold the same entry
        // many times over, which both bloats the index and shows the user duplicate rows.
        // Clean that up once, off the UI thread, then reload the snapshot without them.
        _ = Task.Run(() => RunStartupMaintenanceAsync());
    }

    /// <summary>
    /// One-off database upkeep, run in the background so it never delays startup or a search:
    /// remove duplicate entries left by earlier versions, then return the space they occupied.
    ///
    /// It waits for the first in-memory snapshot to settle first. Both jobs read the same file,
    /// and VACUUM needs it to itself - running them at the same time just means the compaction
    /// loses the race and silently does nothing.
    ///
    /// It is also skipped entirely while indexing is running: a later phase is still writing, so
    /// a duplicate sweep would fight the writer for the connection and compaction would be undone
    /// by the very next chunk.
    /// </summary>
    private async Task RunStartupMaintenanceAsync()
    {
        try
        {
            await _memorySearch.FirstSnapshotSettled;

            if (_indexingService.IsIndexing) return;

            var removed = await _database.EnsureUniqueEntriesAsync();

            // Folder rows the watcher's deletions left behind. Every search scans the folder blob
            // built from this table, so they slow searching down until they are cleared out.
            removed += await _database.PruneOrphanFoldersAsync();

            if (removed > 0)
                _memorySearch.RequestRebuild($"{removed:N0} stale entries removed");

            // Indexes earlier versions built that no query can use - 27% of the file on the
            // database this was measured against. Dropped before the compaction below, which is
            // what returns their pages to the file system.
            await _database.DropUnusedIndexesAsync();

            await _database.CompactIfFragmentedAsync();
        }
        catch (Exception ex)
        {
            // Upkeep is best-effort: a failure here must never stop the app from searching.
            Logger.Log($"Startup database maintenance skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Get total number of indexed items
    /// </summary>
    public async Task<long> GetTotalCountAsync()
    {
        if (_memorySearch.IsReady)
            return _memorySearch.Count;

        if (_useSqlite)
        {
            // COUNT(*) over a large index is slow - never run it on the UI thread
            return await Task.Run(() => _database.GetCountAsync());
        }

        return _indexingService.Status.TotalItems;
    }

    /// <summary>
    /// Discard the index and build it again from scratch. Search is locked until the first phase
    /// publishes again, because there is genuinely nothing to search in the meantime.
    /// </summary>
    public async Task RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        _useSqlite = false;
        _memorySearch.Invalidate();
        SearchSourceChanged?.Invoke(SearchSource.None);
        StatusChanged?.Invoke("Rebuilding the index...");

        await _indexingService.RebuildIndexAsync(cancellationToken);
    }

    /// <summary>
    /// Continue an index that was interrupted, without discarding what is already stored. This is
    /// the same pipeline startup uses, so the Index button and startup behave identically.
    /// </summary>
    public Task ResumeIndexingAsync(CancellationToken cancellationToken = default)
        => _indexingService.ResumeIndexingAsync(cancellationToken);

    /// <summary>
    /// Reconcile the existing index with the disk, picking up everything that changed while the
    /// app was not running (the file watcher only sees changes while it is alive).
    /// </summary>
    public Task RunCatchUpAsync(CancellationToken cancellationToken = default)
        // Thread-pool, not the caller's thread: SQLite's *Async methods run synchronously
        // (see SearchManager.Query.cs), so this would otherwise block the UI at startup.
        => Task.Run(() => _indexingService.RunCatchUpAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Cancel ongoing indexing. Progress already committed is kept, so the next run resumes.
    /// </summary>
    public void CancelIndexing()
    {
        _indexingService.CancelIndexing();
    }

    /// <summary>
    /// Get a status message for display
    /// </summary>
    public string GetStatusMessage()
    {
        if (_indexingService.IsIndexing)
        {
            return IsSearchLocked
                ? "App is indexing..."
                : _indexingService.Status.GetStatusMessage();
        }

        if (_memorySearch.IsReady)
            return $"Instant search ({_memorySearch.Count:N0} items in memory)";

        if (_useSqlite)
            return $"Using local database ({_indexingService.Status.TotalItems:N0} items)";

        return "Search unavailable - no index yet";
    }

    /// <summary>
    /// Bring the background work to a stop and wait for it, so the caller can dispose the
    /// database knowing nothing is still writing to it.
    ///
    /// Await this before <see cref="Dispose"/> on any orderly shutdown path. Dispose alone only
    /// signals cancellation; it returns while the indexing pipeline may still be mid-commit on
    /// the shared connection.
    /// </summary>
    /// <param name="timeout">Upper bound on the wait, so a wedged walk cannot block app exit.</param>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        // Stops new rebuilds being scheduled. The one that may already be running reads through
        // its own private connection, not the shared one, so it cannot be hurt by - nor hurt -
        // the database being disposed after this returns.
        _memorySearch.Dispose();

        await _indexingService.StopAsync(timeout).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _indexingService.DatabaseReady -= OnDatabaseReady;
        _indexingService.ProgressChanged -= OnIndexingProgress;
        _indexingService.IndexingFailed -= OnIndexingFailed;
        _indexingService.ScopePublished -= OnScopePublished;

        _indexingService.Dispose();
        _memorySearch.Dispose();
        _searchGate.Dispose();
    }
}
