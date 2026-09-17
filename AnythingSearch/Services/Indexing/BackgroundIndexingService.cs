using AnythingSearch.Database;
using AnythingSearch.Helper;
using AnythingSearch.Models;
using DeviceDataModule;
using System.Diagnostics;
using System.Threading.Channels;

namespace AnythingSearch.Services;

/// <summary>
/// Phased, resumable background indexer.
///
/// The index is built in three phases, each split into scopes that are published the moment they
/// finish, so the app is searchable in seconds rather than after a full disk walk:
///   Phase 1  the Downloads folder.
///   Phase 2  every non-OS fixed drive, one drive at a time, published per drive.
///   Phase 3  the OS drive, published once it completes.
///
/// Everything about a run is written to indexing_state.json (see <see cref="IndexingState"/>):
/// phase, per-drive status, the units already committed, completion and failures. A restart
/// therefore resumes the missing parts instead of rebuilding 3+ million entries.
///
/// Throughput is deliberately bounded rather than maximal. The previous version ran one walker
/// per CPU core across hundreds of directories on all drives at once, which pinned the disk at
/// 100% and starved the UI. Here one scope runs at a time, with a small number of concurrent
/// walkers and a throttle pause between batches (see <see cref="AppSettings.MaxIndexingThreads"/>,
/// read through <see cref="SettingsService"/>).
///
/// Split into partial classes: this file owns the lifecycle and public API,
/// BackgroundIndexingService.Pipeline.cs the phase/scope/checkpoint loop,
/// BackgroundIndexingService.DirectoryScanning.cs the disk walk,
/// BackgroundIndexingService.Consumer.cs the database writer and progress reporting, and
/// BackgroundIndexingService.CatchUp.cs the reconciliation pass for an index already built.
/// </summary>
public partial class BackgroundIndexingService : IDisposable
{
    private readonly FileDatabase _database;
    private readonly IndexPlanner _planner;
    private readonly DatabaseStatus _status;
    private readonly IndexingState _state;

    /// <summary>Guards <see cref="_state"/>, which is written from parallel walkers.</summary>
    private readonly object _stateLock = new();

    private Channel<FileEntry>? _channel;

    // Lock-free counters
    private long _totalFiles;
    private long _totalFolders;
    private long _scopeFiles;
    private long _scopeFolders;
    private string _currentPath = "";

    private volatile bool _isIndexing;
    private volatile bool _hasSearchableData;
    private volatile string _phaseLabel = "";
    private volatile IndexPhase _currentPhase = IndexPhase.Priority;
    private int _completedScopes;
    private int _totalScopes;

    private Stopwatch _stopwatch = new();
    private Task? _pipelineTask;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;

    // Bounded far below the old 200k: the channel is a buffer, not a staging area, and a large
    // one only holds hundreds of megabytes of FileEntry objects while the single writer catches up.
    private const int ChannelCapacity = 50_000;
    private const int ConsumerBatchSize = 10_000;
    private const int ProgressReportInterval = 20_000;

    public event Action<IndexProgress>? ProgressChanged;
    public event Action? IndexingCompleted;
    public event Action? DatabaseReady;
    public event Action<string>? IndexingFailed;

    /// <summary>
    /// Raised when a scope's entries are committed and its data can be searched. Carries the
    /// scope label, e.g. "Drive D:\".
    /// </summary>
    public event Action<string>? ScopePublished;

    public bool IsIndexing => _isIndexing;

    /// <summary>
    /// Whether anything can be searched yet - true once the first scope has been published, so
    /// the UI can unlock search while later phases are still running in the background.
    /// </summary>
    public bool HasSearchableData => _hasSearchableData;

    public bool IsDatabaseReady => _status.IsReady || _hasSearchableData;

    public DatabaseStatus Status => _status;

    /// <summary>Persisted phase/checkpoint state, exposed for status display.</summary>
    public IndexingState State => _state;

    /// <param name="statusFilePathOverride">
    /// Optional explicit database_status.json path, used by automated tests so they never touch
    /// the real status file under %LocalAppData%. Production code uses the default.
    /// </param>
    /// <param name="stateFilePathOverride">
    /// Same idea for indexing_state.json.
    /// </param>
    public BackgroundIndexingService(
        FileDatabase database,
        string? statusFilePathOverride = null,
        string? stateFilePathOverride = null)
    {
        _database = database;
        _planner = new IndexPlanner();
        _status = DatabaseStatus.Load(statusFilePathOverride);
        _state = IndexingState.Load(stateFilePathOverride
            ?? (statusFilePathOverride == null ? null : statusFilePathOverride + ".state.json"));
    }

    /// <summary>
    /// Open the database and decide what still needs indexing. Returns as soon as the decision is
    /// made - any actual indexing continues in the background.
    /// </summary>
    public async Task InitializeAsync(bool forceReindex = false)
    {
        await _database.InitializeAsync();

        if (forceReindex)
        {
            StartPipeline(fullRebuild: true);
            return;
        }

        var count = await _database.GetCountAsync();

        // State and database must agree. An empty database with recorded progress (a deleted or
        // corrupted file) means the progress is meaningless, so start over.
        if (count == 0 && _state.Scopes.Count > 0)
            _state.Reset();

        SyncPlanWithState();

        if (_state.IsComplete && count > 0)
        {
            _hasSearchableData = true;
            if (!_status.IsReady)
                _status.MarkCompleted(_state.TotalFiles, _state.TotalFolders);

            Logger.Log($"Index already complete with {count:N0} items - nothing to do.");
            DatabaseReady?.Invoke();

            // This release indexes into a versioned data folder, so the previous release's
            // folder is dead weight once a complete index exists here. Retried on every launch
            // that finds a complete index, because the first attempt can fail while an older
            // copy of the app is still holding its database open.
            _ = Task.Run(LegacyDataCleanup.Run);
            _ = Task.Run(() => AppStartupService.ExecuteStartupTaskAsync());
            return;
        }

        // Partial index from an earlier run: what is already there is searchable straight away,
        // and the pipeline picks up where it stopped.
        if (_state.HasSearchableData && count > 0)
        {
            _hasSearchableData = true;
            DatabaseReady?.Invoke();
        }

        Logger.Log($"Resuming indexing - {_state.Scopes.Count(s => s.Status == IndexScopeStatus.Completed)} " +
                   $"of {_state.Scopes.Count} scopes already done.");
        StartPipeline(fullRebuild: false);
    }

    /// <summary>
    /// Continue an interrupted index, or start one if none exists. This and
    /// <see cref="RebuildIndexAsync"/> are the only entry points, so startup indexing and the
    /// Index button share exactly the same pipeline.
    /// </summary>
    public Task ResumeIndexingAsync(CancellationToken cancellationToken = default)
    {
        StartPipeline(fullRebuild: false, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Discard the existing index and build it again from scratch.</summary>
    public async Task RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        // Set before anything else: the database is about to be dropped, so search has to be
        // locked from this moment rather than when the pipeline thread gets around to starting.
        _hasSearchableData = false;

        if (_isIndexing)
        {
            CancelIndexing();
            await WaitForIndexingAsync();
        }

        StartPipeline(fullRebuild: true, cancellationToken);
    }

    /// <summary>
    /// Kick off the pipeline on a background thread. Never blocks the caller, so the UI thread
    /// is free the moment the form is constructed.
    /// </summary>
    private void StartPipeline(bool fullRebuild, CancellationToken cancellationToken = default)
    {
        if (_isIndexing)
        {
            ReportProgress("Indexing already in progress...");
            return;
        }

        _isIndexing = true;
        _stopwatch = Stopwatch.StartNew();
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellationTokenSource.Token;

        _pipelineTask = Task.Run(() => RunPipelineAsync(fullRebuild, token), CancellationToken.None);
    }

    /// <summary>Await the running pipeline. Used by tests and by rebuild, never by the UI thread.</summary>
    public async Task WaitForIndexingAsync()
    {
        var task = _pipelineTask;
        if (task == null) return;

        try { await task; }
        catch (OperationCanceledException) { }
    }

    public void CancelIndexing()
    {
        if (!_isIndexing) return;

        _cancellationTokenSource?.Cancel();
        _channel?.Writer.TryComplete();
    }

    /// <summary>
    /// Cancel the pipeline and wait for it to unwind.
    ///
    /// <see cref="Dispose"/> cannot do this - it is synchronous, and the pipeline can be inside
    /// <c>CommitBatchAsync</c> when it is called. Cancelling without waiting left the walker and
    /// writer still using the shared connection while the owner disposed it, which is why closing
    /// the app during a build could take the process down instead of exiting. Call this, await it,
    /// and only then dispose the database.
    /// </summary>
    /// <param name="timeout">
    /// How long to wait. Cancellation is checked between chunks, so a chunk that has just started
    /// committing has to finish first; the wait is bounded so a wedged walk cannot stop the app
    /// from closing. Progress is not lost on expiry - a chunk is only checkpointed after it
    /// commits, so anything unrecorded is simply re-walked next launch.
    /// </param>
    /// <returns>True if the pipeline finished, false if the timeout expired first.</returns>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        var task = _pipelineTask;
        if (task == null || task.IsCompleted) return true;

        CancelIndexing();

        // No status write here on purpose. Because this awaits the pipeline, the pipeline's own
        // cancellation branch gets to run and records "Indexing was cancelled - progress has been
        // saved" - or MarkCompleted, if it happened to finish on the way out. Marking it failed
        // up front would only be overwritten, and would be wrong in the second case.
        try
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);

            if (completed != task)
            {
                Logger.Log("Indexing shutdown timed out - the pipeline was still running.");
                return false;
            }

            // Observe the result so a fault here is logged rather than resurfacing later as an
            // unobserved task exception.
            await task.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Indexing pipeline faulted while shutting down: {ex.Message}");
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isIndexing)
        {
            // Best effort only. A caller that cares about the pipeline having actually stopped
            // before the database goes away must await StopAsync first - this cannot wait.
            _cancellationTokenSource?.Cancel();
            _channel?.Writer.TryComplete();
            _isIndexing = false;

            _status.MarkFailed("Application closed during indexing");
        }

        _cancellationTokenSource?.Dispose();
    }
}
