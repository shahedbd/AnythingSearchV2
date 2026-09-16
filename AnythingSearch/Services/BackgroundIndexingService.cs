using System.Collections.Concurrent;
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
/// </summary>
public class BackgroundIndexingService : IDisposable
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

    public BackgroundIndexingService(FileDatabase database, SettingsManager settingsManager)
    {
        _database = database;
        _settingsManager = settingsManager;
        _status = DatabaseStatus.Load();
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

    /// <summary>
    /// Collect all root directories, splitting large ones for better parallelism
    /// </summary>
    private List<DirectoryInfo> CollectRootDirectories()
    {
        var result = new List<DirectoryInfo>();

        var downloadsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        var allDrives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .ToList();

        foreach (var drive in allDrives)
        {
            if (drive.Name.ToUpper() == "C:\\")
            {
                if (Directory.Exists(downloadsPath))
                    result.Insert(0, new DirectoryInfo(downloadsPath));

                if (_settingsManager.Settings.IndexSystemDrive)
                {
                    try
                    {
                        var rootDirs = drive.RootDirectory.GetDirectories()
                            .Where(d => !IsExcluded(d.FullName) &&
                                       !d.FullName.Equals(downloadsPath, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var rootDir in rootDirs)
                        {
                            SplitLargeDirectory(rootDir, result);
                        }
                    }
                    catch { }
                }
            }
            else
            {
                try
                {
                    var rootDirs = drive.RootDirectory.GetDirectories()
                        .Where(d => !IsExcluded(d.FullName))
                        .ToList();

                    if (rootDirs.Count > 0)
                    {
                        foreach (var rootDir in rootDirs)
                        {
                            SplitLargeDirectory(rootDir, result);
                        }
                    }
                    else
                    {
                        result.Add(drive.RootDirectory);
                    }
                }
                catch
                {
                    result.Add(drive.RootDirectory);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Split large directories into subdirectories for better parallelism
    /// </summary>
    private void SplitLargeDirectory(DirectoryInfo dir, List<DirectoryInfo> result)
    {
        try
        {
            var subDirs = dir.GetDirectories();
            if (subDirs.Length > 5)
            {
                // Add subdirectories for parallel processing
                result.AddRange(subDirs.Where(d => !IsExcluded(d.FullName)));
                // Also add parent to process its files
                result.Add(dir);
            }
            else
            {
                result.Add(dir);
            }
        }
        catch
        {
            result.Add(dir);
        }
    }

    /// <summary>
    /// Ultra-fast directory scanning optimized for throughput
    /// </summary>
    private void ScanDirectoryFast(DirectoryInfo root, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        var directoryStack = new Stack<DirectoryInfo>(100);
        directoryStack.Push(root);

        var writer = _channel!.Writer;
        var localFolderCount = 0;

        while (directoryStack.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var currentDir = directoryStack.Pop();

            try
            {
                var fullName = currentDir.FullName;
                if (IsExcluded(fullName)) continue;

                _currentPath = fullName;

                // Add folder entry
                var folderEntry = new FileEntry
                {
                    Name = currentDir.Name,
                    Path = fullName,
                    Extension = "",
                    Size = 0,
                    Modified = currentDir.LastWriteTime,
                    IsFolder = true
                };

                while (!writer.TryWrite(folderEntry))
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    Thread.SpinWait(100);
                }
                localFolderCount++;

                // Process files
                try
                {
                    foreach (var file in currentDir.EnumerateFiles())
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        try
                        {
                            var ext = file.Extension;
                            if (IsExcludedExtension(ext)) continue;

                            var fileEntry = new FileEntry
                            {
                                Name = file.Name,
                                Path = file.FullName,
                                Extension = ext.Length > 0 ? ext.Substring(1) : "",
                                Size = file.Length,
                                Modified = file.LastWriteTime,
                                IsFolder = false
                            };

                            while (!writer.TryWrite(fileEntry))
                            {
                                if (cancellationToken.IsCancellationRequested) return;
                                Thread.SpinWait(100);
                            }

                            var total = Interlocked.Increment(ref _totalFiles);
                            if (total % ProgressReportInterval == 0)
                                ReportProgress();
                        }
                        catch { }
                    }
                }
                catch { }

                // Add subdirectories
                try
                {
                    foreach (var subDir in currentDir.EnumerateDirectories())
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        try
                        {
                            var attrs = subDir.Attributes;
                            if ((attrs & FileAttributes.System) == FileAttributes.System &&
                                (attrs & FileAttributes.Hidden) == FileAttributes.Hidden)
                                continue;

                            if (!IsExcluded(subDir.FullName))
                                directoryStack.Push(subDir);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch { }
        }

        Interlocked.Add(ref _totalFolders, localFolderCount);
    }

    /// <summary>
    /// Consumer task - parallel database writer
    /// </summary>
    private async Task ConsumerAsync(int consumerId, CancellationToken cancellationToken)
    {
        var batch = new List<FileEntry>(ConsumerBatchSize);
        var reader = _channel!.Reader;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (batch.Count < ConsumerBatchSize && reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count > 0)
                {
                    foreach (var entry in batch)
                    {
                        await _database.InsertAsync(entry);
                    }
                    Interlocked.Add(ref _processedItems, batch.Count);
                    batch.Clear();
                }
            }

            while (reader.TryRead(out var entry))
            {
                await _database.InsertAsync(entry);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException)
        {
            while (reader.TryRead(out var entry))
            {
                await _database.InsertAsync(entry);
            }
        }
        catch { }
    }

    private bool IsExcluded(string path)
    {
        foreach (var excluded in _settingsManager.Settings.ExcludedFolders)
        {
            if (path.Contains($"\\{excluded}\\", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith($"\\{excluded}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsExcludedExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return _settingsManager.Settings.ExcludedExtensions.Contains(ext);
    }

    private void ReportProgress(string? message = null)
    {
        var elapsed = _stopwatch.Elapsed.TotalSeconds;
        var total = _totalFiles + _totalFolders;
        var speed = elapsed > 0 ? total / elapsed : 0;

        _status.UpdateProgress(_totalFiles, _totalFolders, speed, message ?? _currentPath);

        ProgressChanged?.Invoke(new IndexProgress
        {
            TotalFiles = _totalFiles,
            TotalFolders = _totalFolders,
            CurrentPath = message ?? _currentPath,
            PercentComplete = 0,
            ItemsPerSecond = speed
        });
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