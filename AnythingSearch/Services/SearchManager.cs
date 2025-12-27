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
/// </summary>
public class SearchManager : IDisposable
{
    private readonly WindowsSearchService _windowsSearch;
    private readonly SearchService _sqliteSearch;
    private readonly BackgroundIndexingService _indexingService;
    private readonly FileDatabase _database;

    private bool _useSqlite = false;
    private bool _windowsSearchAvailable = false;
    private bool _disposed = false;

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
        _sqliteSearch = new SearchService(database);
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
    /// Perform a search using the best available source.
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
                var (results, _) = await _sqliteSearch.SearchAsync(query, maxResults);
                return (results, SearchSource.SQLite);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"SQLite search failed: {ex.Message}, falling back to Windows Search");
                // Fall back to Windows Search
                if (_windowsSearchAvailable)
                {
                    var fallbackResults = await _windowsSearch.SearchAsync(query, maxResults, cancellationToken);
                    return (fallbackResults, SearchSource.WindowsSearch);
                }
                return (new List<FileEntry>(), SearchSource.SQLite);
            }
        }
        else if (_windowsSearchAvailable)
        {
            var results = await _windowsSearch.SearchAsync(query, maxResults, cancellationToken);
            return (results, SearchSource.WindowsSearch);
        }
        else
        {
            // Neither available - try SQLite anyway (might have partial data)
            try
            {
                var (results, _) = await _sqliteSearch.SearchAsync(query, maxResults);
                return (results, SearchSource.SQLite);
            }
            catch
            {
                return (new List<FileEntry>(), SearchSource.None);
            }
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

        return await _windowsSearch.SearchContentAsync(query, maxResults, cancellationToken);
    }

    /// <summary>
    /// Get total number of indexed items
    /// </summary>
    public async Task<long> GetTotalCountAsync()
    {
        if (_useSqlite)
        {
            return await _sqliteSearch.GetTotalCountAsync();
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

    #region Event Handlers

    private void OnDatabaseReady()
    {
        _useSqlite = true;
        SearchSourceChanged?.Invoke(SearchSource.SQLite);
        StatusChanged?.Invoke($"Local database ready - {_indexingService.Status.TotalItems:N0} items indexed");
    }

    private void OnIndexingProgress(IndexProgress progress)
    {
        StatusChanged?.Invoke($"Indexing: {progress.TotalFiles + progress.TotalFolders:N0} items ({progress.ItemsPerSecond:N0}/sec)");
    }

    private void OnIndexingFailed(string error)
    {
        StatusChanged?.Invoke($"Indexing failed: {error}");

        // If Windows Search is available, continue using it
        if (_windowsSearchAvailable)
        {
            StatusChanged?.Invoke("Using Windows Search as fallback");
        }
    }

    private void OnWindowsSearchStatus(string status)
    {
        if (!_useSqlite)
        {
            StatusChanged?.Invoke(status);
        }
    }

    #endregion

    #region Expose Indexing Service Events

    /// <summary>
    /// Subscribe to indexing progress
    /// </summary>
    public event Action<IndexProgress>? ProgressChanged
    {
        add => _indexingService.ProgressChanged += value;
        remove => _indexingService.ProgressChanged -= value;
    }

    /// <summary>
    /// Subscribe to indexing completion
    /// </summary>
    public event Action? IndexingCompleted
    {
        add => _indexingService.IndexingCompleted += value;
        remove => _indexingService.IndexingCompleted -= value;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _indexingService.DatabaseReady -= OnDatabaseReady;
        _indexingService.ProgressChanged -= OnIndexingProgress;
        _indexingService.IndexingFailed -= OnIndexingFailed;
        _windowsSearch.StatusChanged -= OnWindowsSearchStatus;

        _indexingService.Dispose();
    }
}

/// <summary>
/// Indicates which search source is being used
/// </summary>
public enum SearchSource
{
    /// <summary>
    /// No search available
    /// </summary>
    None,

    /// <summary>
    /// Using Windows Search Index
    /// </summary>
    WindowsSearch,

    /// <summary>
    /// Using local SQLite database
    /// </summary>
    SQLite
}