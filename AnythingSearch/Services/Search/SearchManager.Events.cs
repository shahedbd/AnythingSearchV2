using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Event wiring side of SearchManager: reacts to BackgroundIndexingService/WindowsSearchService
/// events and re-exposes the indexing events MainForm subscribes to.
/// </summary>
public partial class SearchManager
{
    #region Event Handlers

    private void OnDatabaseReady()
    {
        _useSqlite = true;
        _consecutiveSqliteFailures = 0;
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
    /// Subscribe to the startup catch-up pass that reconciles the index with the disk
    /// </summary>
    public event Action<string>? CatchUpStatusChanged
    {
        add => _indexingService.CatchUpStatusChanged += value;
        remove => _indexingService.CatchUpStatusChanged -= value;
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
}
