using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AnythingSearch.Models;
using AnythingSearch.Database;

namespace AnythingSearch.Services;

/// <summary>
/// High-performance indexing service using maximum parallelization.
/// Optimized for systems with 1M+ files.
/// 
/// Performance optimizations:
/// - Channel-based producer-consumer pattern (faster than ConcurrentQueue)
/// - Parallel.ForEach with configurable degree of parallelism
/// - Multiple consumer tasks for database writes
/// - Batch processing with optimal batch sizes
/// - Lock-free counters using Interlocked
/// - Memory-efficient directory scanning
/// </summary>
public class IndexingService : IDisposable
{
    private readonly FileDatabase _database;
    private readonly SettingsManager _settingsManager;

    // High-performance channel for producer-consumer pattern
    private Channel<FileEntry>? _channel;

    // Statistics using lock-free operations
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

    // Performance tuning constants
    private const int ChannelCapacity = 200_000;           // Larger buffer
    private const int ConsumerCount = 1;                   // Single consumer - DB has internal lock
    private const int ConsumerBatchSize = 20000;           // Larger batches
    private const int ProgressReportInterval = 50000;      // Less frequent reporting
    private const int MaxDegreeOfParallelism = -1;         // -1 = use all processors

    public event Action<IndexProgress>? ProgressChanged;
    public event Action? IndexingCompleted;
    public bool IsIndexing => _isIndexing;

    public IndexingService(FileDatabase database, SettingsManager settingsManager)
    {
        _database = database;
        _settingsManager = settingsManager;
    }

    /// <summary>
    /// Start full background indexing - maximum parallelization
    /// Returns immediately, indexing continues in background
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

        // Create bounded channel for backpressure support
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
            ReportProgress($"Database initialization failed: {ex.Message}");
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
                ReportProgress($"Starting high-performance index using {processorCount} processors...");

                // Collect all root directories to scan
                var allDirectoriesToScan = CollectRootDirectories();

                ReportProgress($"Scanning {allDirectoriesToScan.Count} root directories in parallel...");

                // Use Parallel.ForEach for maximum CPU utilization
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxDegreeOfParallelism == -1 ? processorCount : MaxDegreeOfParallelism,
                    CancellationToken = token
                };

                // Scan all directories in parallel
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

                // Signal that scanning is complete
                _scanningComplete = true;
                _channel?.Writer.Complete();

                ReportProgress("Waiting for database writes to complete...");

                // Wait for all consumers to finish
                if (_consumerTasks != null)
                {
                    await Task.WhenAll(_consumerTasks);
                }

                _stopwatch.Stop();

                // Finalize database
                await _database.CommitBatchAsync();
                await _database.FinalizeIndexingAsync();

                var totalCount = await _database.GetCountAsync();
                var totalTime = _stopwatch.Elapsed;
                var speed = totalTime.TotalSeconds > 0 ? totalCount / totalTime.TotalSeconds : 0;

                _isIndexing = false;
                ReportProgress($"✓ Complete! {totalCount:N0} items in {totalTime:mm\\:ss} ({speed:N0}/sec)");

                IndexingCompleted?.Invoke();
            }
            catch (OperationCanceledException)
            {
                _isIndexing = false;
                _channel?.Writer.TryComplete();
                ReportProgress("Indexing cancelled");
            }
            catch (Exception ex)
            {
                _isIndexing = false;
                _channel?.Writer.TryComplete();
                ReportProgress($"Indexing error: {ex.Message}");
            }
        }, token);
    }

    /// <summary>
    /// Collect all root directories to scan
    /// </summary>
    private List<DirectoryInfo> CollectRootDirectories()
    {
        var allDirectoriesToScan = new List<DirectoryInfo>();

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
                // Add Downloads first (priority)
                if (Directory.Exists(downloadsPath))
                {
                    allDirectoriesToScan.Insert(0, new DirectoryInfo(downloadsPath));
                }

                if (_settingsManager.Settings.IndexSystemDrive)
                {
                    try
                    {
                        // Add each root folder separately for better parallelism
                        var rootDirs = drive.RootDirectory.GetDirectories()
                            .Where(d => !IsExcluded(d.FullName) &&
                                       !d.FullName.Equals(downloadsPath, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        // Add subfolders of large directories for even better parallelism
                        foreach (var rootDir in rootDirs)
                        {
                            try
                            {
                                var subDirs = rootDir.GetDirectories();
                                if (subDirs.Length > 10)
                                {
                                    // Split large directories into their subdirectories
                                    allDirectoriesToScan.AddRange(subDirs.Where(d => !IsExcluded(d.FullName)));
                                    // Also add files in the root directory itself
                                    allDirectoriesToScan.Add(rootDir);
                                }
                                else
                                {
                                    allDirectoriesToScan.Add(rootDir);
                                }
                            }
                            catch
                            {
                                allDirectoriesToScan.Add(rootDir);
                            }
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
                        // Split large directories for better parallelism
                        foreach (var rootDir in rootDirs)
                        {
                            try
                            {
                                var subDirs = rootDir.GetDirectories();
                                if (subDirs.Length > 10)
                                {
                                    allDirectoriesToScan.AddRange(subDirs.Where(d => !IsExcluded(d.FullName)));
                                    allDirectoriesToScan.Add(rootDir);
                                }
                                else
                                {
                                    allDirectoriesToScan.Add(rootDir);
                                }
                            }
                            catch
                            {
                                allDirectoriesToScan.Add(rootDir);
                            }
                        }
                    }
                    else
                    {
                        allDirectoriesToScan.Add(drive.RootDirectory);
                    }
                }
                catch
                {
                    allDirectoriesToScan.Add(drive.RootDirectory);
                }
            }
        }

        return allDirectoriesToScan;
    }

    /// <summary>
    /// Ultra-fast directory scanning using stack-based iteration
    /// Optimized for minimal allocations and maximum throughput
    /// </summary>
    private void ScanDirectoryFast(DirectoryInfo root, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        var directoryStack = new Stack<DirectoryInfo>(100);
        directoryStack.Push(root);

        var writer = _channel!.Writer;
        var localFileCount = 0;
        var localFolderCount = 0;

        while (directoryStack.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var currentDir = directoryStack.Pop();

            try
            {
                var fullName = currentDir.FullName;
                if (IsExcluded(fullName))
                    continue;

                // Update current path for progress reporting
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

                // Use synchronous write for speed (channel handles backpressure)
                while (!writer.TryWrite(folderEntry))
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    Thread.SpinWait(100);
                }

                localFolderCount++;

                // Process files using EnumerateFiles for memory efficiency
                try
                {
                    foreach (var file in currentDir.EnumerateFiles())
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        try
                        {
                            var ext = file.Extension;
                            if (IsExcludedExtension(ext))
                                continue;

                            var fileEntry = new FileEntry
                            {
                                Name = file.Name,
                                Path = file.FullName,
                                Extension = ext.Length > 0 ? ext.Substring(1) : "", // Skip the dot
                                Size = file.Length,
                                Modified = file.LastWriteTime,
                                IsFolder = false
                            };

                            while (!writer.TryWrite(fileEntry))
                            {
                                if (cancellationToken.IsCancellationRequested) return;
                                Thread.SpinWait(100);
                            }

                            localFileCount++;

                            // Update global counter and report progress periodically
                            var total = Interlocked.Increment(ref _totalFiles);
                            if (total % ProgressReportInterval == 0)
                            {
                                ReportProgress();
                            }
                        }
                        catch (UnauthorizedAccessException) { }
                        catch (PathTooLongException) { }
                        catch (IOException) { }
                        catch { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
                catch { }

                // Add subdirectories to stack
                try
                {
                    foreach (var subDir in currentDir.EnumerateDirectories())
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        try
                        {
                            var attrs = subDir.Attributes;
                            // Skip hidden system directories
                            if ((attrs & FileAttributes.System) == FileAttributes.System &&
                                (attrs & FileAttributes.Hidden) == FileAttributes.Hidden)
                                continue;

                            if (!IsExcluded(subDir.FullName))
                                directoryStack.Push(subDir);
                        }
                        catch { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
                catch { }
            }
            catch (UnauthorizedAccessException) { }
            catch (PathTooLongException) { }
            catch (DirectoryNotFoundException) { }
            catch { }
        }

        // Update global counters
        Interlocked.Add(ref _totalFolders, localFolderCount);
    }

    /// <summary>
    /// Consumer task - reads from channel and writes to database in batches
    /// Multiple consumers run in parallel for maximum throughput
    /// </summary>
    private async Task ConsumerAsync(int consumerId, CancellationToken cancellationToken)
    {
        var batch = new List<FileEntry>(ConsumerBatchSize);
        var reader = _channel!.Reader;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                // Read as many items as available up to batch size
                while (batch.Count < ConsumerBatchSize && reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                // Process batch
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

            // Process any remaining items
            while (reader.TryRead(out var entry))
            {
                await _database.InsertAsync(entry);
                Interlocked.Increment(ref _processedItems);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (ChannelClosedException)
        {
            // Channel was closed, process remaining items
            while (reader.TryRead(out var entry))
            {
                await _database.InsertAsync(entry);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Consumer {consumerId} error: {ex.Message}");
        }
    }

    private bool IsExcluded(string path)
    {
        var excludedFolders = _settingsManager.Settings.ExcludedFolders;
        foreach (var excluded in excludedFolders)
        {
            if (path.Contains($"\\{excluded}\\", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith($"\\{excluded}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
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
        var totalScanned = _totalFiles + _totalFolders;
        var speed = elapsed > 0 ? totalScanned / elapsed : 0;

        ProgressChanged?.Invoke(new IndexProgress
        {
            TotalFiles = _totalFiles,
            TotalFolders = _totalFolders,
            CurrentPath = message ?? _currentPath,
            PercentComplete = 0,
            ItemsPerSecond = speed
        });
    }

    public async Task WaitForBackgroundIndexingAsync()
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
        }

        _cancellationTokenSource?.Dispose();
    }
}