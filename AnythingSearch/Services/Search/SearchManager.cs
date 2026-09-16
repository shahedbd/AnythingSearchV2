using AnythingSearch.Models;
using AnythingSearch.Database;

namespace AnythingSearch.Services;

/// <summary>
/// Manages search operations, automatically switching between Windows Search
/// and the SQLite database based on database readiness.
/// 
/// Strategy:
/// 1. On startup, use Windows Search for immediate results (if available)
/// 2. Background indexing runs in parallel to build SQLite database
/// 3. Once SQLite is ready, switch to it for faster, more complete results
/// 4. Falls back to Windows Search if SQLite fails
///
/// Split into partial classes: this file owns initialization and the rebuild/status API;
/// see SearchManager.Query.cs for search execution and SearchManager.Events.cs for the
/// indexing/status event wiring.
/// </summary>
public partial class SearchManager : IDisposable
{
    private readonly WindowsSearchService _windowsSearch;
    private readonly BackgroundIndexingService _indexingService;
    private readonly FileDatabase _database;

    private bool _useSqlite = false;
    private bool _windowsSearchAvailable = false;
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
    public SearchSource CurrentSource => _useSqlite ? SearchSource.SQLite : SearchSource.WindowsSearch;

    /// <summary>
    /// Whether the SQLite database is ready
    /// </summary>
    public bool IsDatabaseReady => _indexingService.IsDatabaseReady;

    /// <summary>
    /// Whether Windows Search is available
    /// </summary>
    public bool IsWindowsSearchAvailable => _windowsSearchAvailable;

    /// <summary>
    /// Current indexing progress
    /// </summary>
    public DatabaseStatus IndexingStatus => _indexingService.Status;

    /// <summary>
    /// Whether indexing is in progress
    /// </summary>
    public bool IsIndexing => _indexingService.IsIndexing;

    public SearchManager(
        FileDatabase database,
        SettingsManager settingsManager)
    {
        _database = database;
        _windowsSearch = new WindowsSearchService();
        _indexingService = new BackgroundIndexingService(database, settingsManager);

        // Subscribe to indexing events
        _indexingService.DatabaseReady += OnDatabaseReady;
        _indexingService.ProgressChanged += OnIndexingProgress;
        _indexingService.IndexingFailed += OnIndexingFailed;

        // Subscribe to Windows Search status
        _windowsSearch.StatusChanged += OnWindowsSearchStatus;
    }

    /// <summary>
    /// Initialize the search manager.
    /// Checks Windows Search availability and starts background indexing if needed.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Check Windows Search availability
        _windowsSearchAvailable = await _windowsSearch.IsAvailableAsync();

        if (_windowsSearchAvailable)
        {
            StatusChanged?.Invoke("Windows Search available - checking local database...");
        }
        else
        {
            StatusChanged?.Invoke("Windows Search not available - checking local database...");
        }

        // Initialize the indexing service (will check if DB is ready or start indexing)
        await _indexingService.InitializeAsync();

        // Check if database is already ready (either was ready or just became ready)
        if (_indexingService.IsDatabaseReady)
        {
            _useSqlite = true;
            SearchSourceChanged?.Invoke(SearchSource.SQLite);
            StatusChanged?.Invoke($"Using local database ({_indexingService.Status.TotalItems:N0} items)");
            System.Diagnostics.Debug.WriteLine($"[SearchManager] Database is ready, using SQLite");
        }
        else if (_indexingService.IsIndexing)
        {
            // Indexing started, use Windows Search in the meantime
            if (_windowsSearchAvailable)
            {
                StatusChanged?.Invoke("Building local database... using Windows Search temporarily");
            }
            else
            {
                StatusChanged?.Invoke("Building local database...");
            }
            System.Diagnostics.Debug.WriteLine($"[SearchManager] Indexing in progress, using Windows Search");
        }
    }

    /// <summary>
    /// Perform content search (searches inside files).
    /// Only available with Windows Search.
    /// </summary>
    public async Task<List<FileEntry>> SearchContentAsync(
        string query,
        int maxResults = 500,
        CancellationToken cancellationToken = default)
    {
        if (!_windowsSearchAvailable)
        {
            StatusChanged?.Invoke("Content search requires Windows Search");
            return new List<FileEntry>();
        }

        // OLE DB has no real async I/O - keep it off the UI thread
        return await Task.Run(
            () => _windowsSearch.SearchContentAsync(query, maxResults, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Get total number of indexed items
    /// </summary>
    public async Task<long> GetTotalCountAsync()
    {
        if (_useSqlite)
        {
            // COUNT(*) over a large index is slow - never run it on the UI thread
            return await Task.Run(() => _database.GetCountAsync());
        }
        else
        {
            return _indexingService.Status.TotalItems;
        }
    }

    /// <summary>
    /// Force rebuild of the SQLite index
    /// </summary>
    public async Task RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        // Temporarily switch to Windows Search during rebuild
        if (_windowsSearchAvailable)
        {
            _useSqlite = false;
            SearchSourceChanged?.Invoke(SearchSource.WindowsSearch);
            StatusChanged?.Invoke("Rebuilding index - using Windows Search temporarily");
        }

        await _indexingService.RebuildIndexAsync(cancellationToken);
    }

    /// <summary>
    /// Reconcile the existing index with the disk, picking up everything that changed while the
    /// app was not running (the file watcher only sees changes while it is alive).
    /// </summary>
    public Task RunCatchUpAsync(CancellationToken cancellationToken = default)
        // Thread-pool, not the caller's thread: SQLite's *Async methods run synchronously
        // (see SearchManager.Query.cs), so this would otherwise block the UI at startup.
        => Task.Run(() => _indexingService.RunCatchUpAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Cancel ongoing indexing
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
            return _indexingService.Status.GetStatusMessage();
        }
        else if (_useSqlite)
        {
            return $"Using local database ({_indexingService.Status.TotalItems:N0} items)";
        }
        else if (_windowsSearchAvailable)
        {
            return "Using Windows Search Index";
        }
        else
        {
            return "Search unavailable";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _indexingService.DatabaseReady -= OnDatabaseReady;
        _indexingService.ProgressChanged -= OnIndexingProgress;
        _indexingService.IndexingFailed -= OnIndexingFailed;
        _windowsSearch.StatusChanged -= OnWindowsSearchStatus;

        _indexingService.Dispose();
        _searchGate.Dispose();
    }
}