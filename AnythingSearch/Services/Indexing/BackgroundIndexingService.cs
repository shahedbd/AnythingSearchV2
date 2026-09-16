using System.Diagnostics;
using System.Threading.Channels;
using AnythingSearch.Models;
using AnythingSearch.Database;

namespace AnythingSearch.Services;

/// <summary>
/// High-performance background indexing service.
/// Uses maximum parallelization for fastest indexing.
///
/// Performance optimizations:
/// - Channel-based producer-consumer pattern
/// - Parallel.ForEach with all CPU cores
/// - Multiple consumer tasks for database writes
/// - Batch processing with optimal sizes
/// - Lock-free counters
///
/// Split into partial classes: this file owns the lifecycle (init/start/cancel/rebuild/dispose);
/// see BackgroundIndexingService.DirectoryScanning.cs for the disk-walking producer side and
/// BackgroundIndexingService.Consumer.cs for the database-writing consumer side.
/// </summary>
public partial class BackgroundIndexingService : IDisposable
{
    private readonly FileDatabase _database;
    private readonly SettingsManager _settingsManager;
    private readonly DatabaseStatus _status;

    // High-performance channel
    private Channel<FileEntry>? _channel;

    // Lock-free counters
    private long _totalFiles = 0;
    private long _totalFolders = 0;
    private long _processedItems = 0;
    private string _currentPath = "";
    private volatile bool _isIndexing = false;
    private volatile bool _scanningComplete = false;

    private Stopwatch _stopwatch = new();
    private Task? _backgroundIndexingTask;
    private Task[]? _consumerTasks;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed = false;

    // Performance tuning - adjust based on your system
    private const int ChannelCapacity = 200_000;
    private const int ConsumerCount = 1;               // Single consumer - DB flush has lock
    private const int ConsumerBatchSize = 20000;       // Larger batches
    private const int ProgressReportInterval = 50000;  // Less frequent reporting

    public event Action<IndexProgress>? ProgressChanged;
    public event Action? IndexingCompleted;
    public event Action? DatabaseReady;
    public event Action<string>? IndexingFailed;

    public bool IsIndexing => _isIndexing;
    public bool IsDatabaseReady => _status.IsReady;
    public DatabaseStatus Status => _status;

    /// <param name="statusFilePathOverride">
    /// Optional explicit database_status.json path, used by automated tests so they never touch
    /// the real status file under %LocalAppData%. Production code uses the default.
    /// </param>
    public BackgroundIndexingService(
        FileDatabase database,
        SettingsManager settingsManager,
        string? statusFilePathOverride = null)
    {
        _database = database;
        _settingsManager = settingsManager;
        _status = DatabaseStatus.Load(statusFilePathOverride);
    }

    /// <summary>
    /// Initialize the service and check if database is ready.
    /// </summary>
    public async Task InitializeAsync(bool forceReindex = false)
    {
        await _database.InitializeAsync();

        if (forceReindex)
        {
            _status.Reset();
            _ = StartBackgroundIndexAsync();
            return;
        }

        if (_status.IsReady)
        {
            var count = await _database.GetCountAsync();
            System.Diagnostics.Debug.WriteLine($"[BackgroundIndexingService] Status is Ready, DB count: {count}");

            if (count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[BackgroundIndexingService] Database ready with {count} items.");
                DatabaseReady?.Invoke();
                return;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[BackgroundIndexingService] DB empty, resetting.");
                _status.Reset();
            }
        }

        if (_status.State == DatabaseState.Failed)
        {
            System.Diagnostics.Debug.WriteLine($"[BackgroundIndexingService] Previous failed: {_status.ErrorMessage}");
            _status.Reset();
        }

        if (_status.State == DatabaseState.NotStarted)
        {
            System.Diagnostics.Debug.WriteLine($"[BackgroundIndexingService] Starting indexing...");
            _ = StartBackgroundIndexAsync();
        }
    }

    /// <summary>
    /// Start full background indexing with maximum parallelization.
    /// Returns immediately, indexing continues in background.
    /// </summary>
    public async Task StartBackgroundIndexAsync(CancellationToken cancellationToken = default)
    {
        if (_isIndexing)
        {
            ReportProgress("Indexing already in progress...");
            return;
        }

        _isIndexing = true;
        _scanningComplete = false;
        _stopwatch = Stopwatch.StartNew();
        _totalFiles = 0;
        _totalFolders = 0;
        _processedItems = 0;

        _status.MarkIndexingStarted();

        // Create high-performance bounded channel
        _channel = Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellationTokenSource.Token;

        try
        {
            await _database.ClearAsync();
            await _database.BeginBatchAsync();
        }
        catch (Exception ex)
        {
            _isIndexing = false;
            _status.MarkFailed($"Database initialization failed: {ex.Message}");
            IndexingFailed?.Invoke(ex.Message);
            return;
        }

        // Start multiple consumer tasks for parallel DB writes
        _consumerTasks = new Task[ConsumerCount];
        for (int i = 0; i < ConsumerCount; i++)
        {
            int consumerId = i;
            _consumerTasks[i] = Task.Run(() => ConsumerAsync(consumerId, token), token);
        }

        // Start background indexing
        _backgroundIndexingTask = Task.Run(async () =>
        {
            try
            {
                var processorCount = Environment.ProcessorCount;
                ReportProgress($"Starting high-performance index ({processorCount} CPUs)...");

                var allDirectoriesToScan = CollectRootDirectories();
                ReportProgress($"Scanning {allDirectoriesToScan.Count} directories in parallel...");

                // Use Parallel.ForEach for maximum CPU utilization
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = processorCount,
                    CancellationToken = token
                };

                await Task.Run(() =>
                {
                    Parallel.ForEach(allDirectoriesToScan, parallelOptions, directory =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            ScanDirectoryFast(directory, token);
                        }
                    });
                }, token);

                _scanningComplete = true;
                _channel?.Writer.Complete();

                ReportProgress("Waiting for database writes...");

                if (_consumerTasks != null)
                {
                    await Task.WhenAll(_consumerTasks);
                }

                _stopwatch.Stop();

                await _database.CommitBatchAsync();
                await _database.FinalizeIndexingAsync();

                var totalCount = await _database.GetCountAsync();
                var totalTime = _stopwatch.Elapsed;
                var speed = totalTime.TotalSeconds > 0 ? totalCount / totalTime.TotalSeconds : 0;

                _isIndexing = false;
                _status.MarkCompleted(_totalFiles, _totalFolders);

                ReportProgress($"✓ Complete! {totalCount:N0} items in {totalTime:mm\\:ss} ({speed:N0}/sec)");

                IndexingCompleted?.Invoke();
                DatabaseReady?.Invoke();
            }
            catch (OperationCanceledException)
            {
                _isIndexing = false;
                _channel?.Writer.TryComplete();
                _status.MarkFailed("Indexing was cancelled");
                ReportProgress("Indexing cancelled");
            }
            catch (Exception ex)
            {
                _isIndexing = false;
                _channel?.Writer.TryComplete();
                _status.MarkFailed(ex.Message);
                ReportProgress($"Indexing error: {ex.Message}");
                IndexingFailed?.Invoke(ex.Message);
            }
        }, token);
    }

    public async Task WaitForIndexingAsync()
    {
        if (_backgroundIndexingTask != null)
            await _backgroundIndexingTask;
    }

    public void CancelIndexing()
    {
        if (!_isIndexing) return;

        _cancellationTokenSource?.Cancel();
        _channel?.Writer.TryComplete();
        _isIndexing = false;
        _status.MarkFailed("Indexing was cancelled by user");
    }

    public async Task RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        if (_isIndexing)
        {
            CancelIndexing();
            await Task.Delay(500);
        }

        _status.Reset();
        await StartBackgroundIndexAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isIndexing)
        {
            _cancellationTokenSource?.Cancel();
            _channel?.Writer.TryComplete();
            _isIndexing = false;
            _status.MarkFailed("Application closed during indexing");
        }

        _cancellationTokenSource?.Dispose();
    }
}
