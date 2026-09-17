using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Event wiring side of SearchManager: reacts to BackgroundIndexingService events and re-exposes
/// the indexing events MainForm subscribes to.
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

        // There is something to load now, so pull the index into RAM for instant searching.
        _memorySearch.RequestRebuild("database ready");
    }

    /// <summary>
    /// One indexing scope has been committed - phase 1, or one drive. Its entries are folded into
    /// the in-memory index straight away, which is what makes a drive searchable the moment it
    /// finishes instead of at the end of the whole run.
    /// </summary>
    private void OnScopePublished(string scopeLabel)
    {
        _useSqlite = true;
        _consecutiveSqliteFailures = 0;
        SearchSourceChanged?.Invoke(CurrentSource);
        _memorySearch.RequestRebuild($"{scopeLabel} indexed");
    }

    private void OnIndexingProgress(IndexProgress progress)
    {
        StatusChanged?.Invoke(
            $"{progress.PhaseLabel}: {progress.TotalFiles + progress.TotalFolders:N0} items " +
            $"({progress.ItemsPerSecond:N0}/sec)");
    }

    private void OnIndexingFailed(string error)
    {
        StatusChanged?.Invoke($"Indexing failed: {error}");
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

    /// <summary>
    /// Subscribe to the moment one phase or drive becomes searchable. Carries the scope label.
    /// </summary>
    public event Action<string>? ScopePublished
    {
        add => _indexingService.ScopePublished += value;
        remove => _indexingService.ScopePublished -= value;
    }

    #endregion
}
