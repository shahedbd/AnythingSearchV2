using System.Collections.Concurrent;
using AnythingSearch.Models;
using AnythingSearch.Database;
using AnythingSearch.Services.Search.Memory;
using Timer = System.Threading.Timer;

namespace AnythingSearch.Services;

/// <summary>
/// Watches the file system and keeps the SQLite index in sync after the initial build.
///
/// Split into partial classes: this file owns watcher lifecycle and raw OS event wiring;
/// see FileWatcherService.ChangeQueue.cs for the debounce/batch queue and
/// FileWatcherService.SyncHandlers.cs for the database sync logic per change type.
/// </summary>
public partial class FileWatcherService : IDisposable
{
    private readonly FileDatabase _database;
    private readonly SettingsManager _settingsManager;

    // Every change written to SQLite is mirrored here so the in-memory index stays current
    // between snapshot rebuilds. Null until the search manager hands it over.
    private MemorySearchService? _memoryIndex;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, FileSystemChange> _pendingChanges = new();
    private readonly Timer _processTimer;
    private readonly object _lock = new();
    private bool _isRunning = false;
    private bool _isProcessing = false;
    private DateTime _lastProcessTime = DateTime.MinValue;

    // Buffer overflow protection
    private const int MaxPendingChanges = 10000;
    private const int HighWaterMark = MaxPendingChanges / 2;
    private const int ProcessIntervalMs = 3000; // Process every 3 seconds
    private const int DebounceMs = 500; // Ignore duplicate changes within 500ms
    private const int MaxChangeAgeMs = 10000; // Flush a queued path after 10s even if it keeps changing

    public event Action<string>? StatusChanged;
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Mirror changes into the in-memory search index as well as the database, so a file created
    /// a second ago is findable without waiting for the next snapshot rebuild.
    /// </summary>
    public void AttachMemoryIndex(MemorySearchService memoryIndex) => _memoryIndex = memoryIndex;

    public FileWatcherService(FileDatabase database, SettingsManager settingsManager)
    {
        _database = database;
        _settingsManager = settingsManager;

        // Process changes periodically (batch processing)
        _processTimer = new Timer(ProcessChangesCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Start monitoring all drives
    /// </summary>
    public void StartWatching()
    {
        lock (_lock)
        {
            if (_isRunning) return;

            _isRunning = true;
            StatusChanged?.Invoke("Starting file system monitoring...");

            // Watch all fixed drives
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .ToList();

            foreach (var drive in drives)
            {
                try
                {
                    var watcher = CreateWatcher(drive.RootDirectory.FullName);
                    if (watcher != null)
                    {
                        _watchers.Add(watcher);
                        StatusChanged?.Invoke($"Monitoring: {drive.Name}");
                    }
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"Failed to monitor {drive.Name}: {ex.Message}");
                }
            }

            // Start processing timer
            _processTimer.Change(ProcessIntervalMs, ProcessIntervalMs);

            StatusChanged?.Invoke($"✓ Monitoring {_watchers.Count} drive(s) for changes");
        }
    }

    /// <summary>
    /// Create a FileSystemWatcher with optimal settings
    /// </summary>
    private FileSystemWatcher? CreateWatcher(string path)
    {
        try
        {
            var watcher = new FileSystemWatcher
            {
                Path = path,
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.CreationTime,
                Filter = "*.*",
                // Increase buffer size to handle high-volume changes
                InternalBufferSize = 65536 // 64KB (default is 8KB)
            };

            // Subscribe to events
            watcher.Created += OnCreated;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Changed += OnChanged;
            watcher.Error += OnError;

            // Start watching
            watcher.EnableRaisingEvents = true;

            return watcher;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Stop monitoring
    /// </summary>
    public void StopWatching()
    {
        lock (_lock)
        {
            if (!_isRunning) return;

            _isRunning = false;
            _processTimer.Change(Timeout.Infinite, Timeout.Infinite);

            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= OnCreated;
                    watcher.Deleted -= OnDeleted;
                    watcher.Renamed -= OnRenamed;
                    watcher.Changed -= OnChanged;
                    watcher.Error -= OnError;
                    watcher.Dispose();
                }
                catch { }
            }

            _watchers.Clear();
            _pendingChanges.Clear();

            StatusChanged?.Invoke("File system monitoring stopped");
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        QueueChange(e.FullPath, ChangeType.Created);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        QueueChange(e.FullPath, ChangeType.Deleted);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        QueueChange(e.FullPath, ChangeType.Renamed, e.OldFullPath);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        QueueChange(e.FullPath, ChangeType.Modified);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        StatusChanged?.Invoke($"⚠ Watcher error: {ex?.Message}");

        // Try to restart the watcher that failed
        if (sender is FileSystemWatcher failedWatcher)
        {
            Task.Run(async () =>
            {
                await Task.Delay(5000); // Wait 5 seconds before restart
                RestartWatcher(failedWatcher);
            });
        }
    }

    /// <summary>
    /// Restart a failed watcher
    /// </summary>
    private void RestartWatcher(FileSystemWatcher failedWatcher)
    {
        lock (_lock)
        {
            if (!_isRunning) return;

            try
            {
                var path = failedWatcher.Path;

                // Remove old watcher
                _watchers.Remove(failedWatcher);
                try { failedWatcher.Dispose(); } catch { }

                // Create new watcher
                var newWatcher = CreateWatcher(path);
                if (newWatcher != null)
                {
                    _watchers.Add(newWatcher);
                    StatusChanged?.Invoke($"✓ Restarted monitoring: {path}");
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Failed to restart watcher: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Get current status information
    /// </summary>
    public (int WatcherCount, int PendingChanges, bool IsRunning) GetStatus()
    {
        return (_watchers.Count, _pendingChanges.Count, _isRunning);
    }

    public void Dispose()
    {
        StopWatching();
        _processTimer?.Dispose();
    }
}
