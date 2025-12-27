using System.Collections.Concurrent;
using AnythingSearch.Models;
using AnythingSearch.Database;
using Timer = System.Threading.Timer;

namespace AnythingSearch.Services;

public class FileWatcherService : IDisposable
{
    private readonly FileDatabase _database;
    private readonly SettingsManager _settingsManager;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, FileSystemChange> _pendingChanges = new();
    private readonly Timer _processTimer;
    private readonly object _lock = new();
    private bool _isRunning = false;
    private bool _isProcessing = false;
    private DateTime _lastProcessTime = DateTime.MinValue;

    // Buffer overflow protection
    private const int MaxPendingChanges = 10000;
    private const int ProcessIntervalMs = 3000; // Process every 3 seconds
    private const int DebounceMs = 500; // Ignore duplicate changes within 500ms

    public event Action<string>? StatusChanged;
    public bool IsRunning => _isRunning;

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
    /// Queue a change for processing with debouncing
    /// </summary>
    private void QueueChange(string path, ChangeType type, string? oldPath = null)
    {
        if (!_isRunning) return;
        if (ShouldIgnore(path)) return;

        // Prevent buffer overflow
        if (_pendingChanges.Count >= MaxPendingChanges)
        {
            StatusChanged?.Invoke("⚠ Too many pending changes, some may be missed");
            return;
        }

        var change = new FileSystemChange
        {
            Type = type,
            Path = path,
            OldPath = oldPath,
            Timestamp = DateTime.Now
        };

        // Use path as key for automatic deduplication
        // Later changes for same path will overwrite earlier ones
        _pendingChanges.AddOrUpdate(path, change, (key, existing) =>
        {
            // Keep the more important change type
            // Delete > Rename > Create > Modified
            if (type == ChangeType.Deleted ||
                (type == ChangeType.Renamed && existing.Type != ChangeType.Deleted) ||
                (type == ChangeType.Created && existing.Type == ChangeType.Modified))
            {
                return change;
            }

            // Update timestamp for debouncing
            existing.Timestamp = DateTime.Now;
            return existing;
        });
    }

    /// <summary>
    /// Process queued changes (called by timer)
    /// </summary>
    private void ProcessChangesCallback(object? state)
    {
        // Prevent concurrent processing
        if (_isProcessing) return;

        Task.Run(async () => await ProcessChangesAsync());
    }

    /// <summary>
    /// Process all pending changes
    /// </summary>
    private async Task ProcessChangesAsync()
    {
        if (_isProcessing || _pendingChanges.IsEmpty) return;

        _isProcessing = true;

        try
        {
            // Get all changes that are old enough (debounced)
            var cutoffTime = DateTime.Now.AddMilliseconds(-DebounceMs);
            var changesToProcess = _pendingChanges
                .Where(kvp => kvp.Value.Timestamp < cutoffTime)
                .Select(kvp => kvp.Value)
                .ToList();

            if (changesToProcess.Count == 0)
            {
                _isProcessing = false;
                return;
            }

            // Remove processed items from dictionary
            foreach (var change in changesToProcess)
            {
                _pendingChanges.TryRemove(change.Path, out _);
            }

            // Sort: process deletes first, then creates, then renames, then modifications
            var sortedChanges = changesToProcess
                .OrderBy(c => c.Type switch
                {
                    ChangeType.Deleted => 0,
                    ChangeType.Created => 1,
                    ChangeType.Renamed => 2,
                    ChangeType.Modified => 3,
                    _ => 4
                })
                .ToList();

            int processed = 0;
            int errors = 0;

            // Process each change individually (no batch transaction)
            foreach (var change in sortedChanges)
            {
                try
                {
                    switch (change.Type)
                    {
                        case ChangeType.Created:
                            await HandleCreatedAsync(change.Path);
                            processed++;
                            break;

                        case ChangeType.Deleted:
                            await HandleDeletedAsync(change.Path);
                            processed++;
                            break;

                        case ChangeType.Renamed:
                            if (!string.IsNullOrEmpty(change.OldPath))
                            {
                                await HandleRenamedAsync(change.OldPath, change.Path);
                                processed++;
                            }
                            break;

                        case ChangeType.Modified:
                            await HandleModifiedAsync(change.Path);
                            processed++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"FileWatcher error: {ex.Message}");
                    errors++;
                }
            }

            if (processed > 0)
            {
                var message = errors > 0
                    ? $"Auto-watch: {processed} change(s), {errors} error(s)"
                    : $"Auto-watch: {processed} change(s) synced";
                StatusChanged?.Invoke(message);
            }

            _lastProcessTime = DateTime.Now;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Auto-watch: Error processing changes: {ex.Message}");
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async Task HandleCreatedAsync(string path)
    {
        try
        {
            // Check if already exists in database
            if (await _database.ExistsAsync(path))
                return;

            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                await _database.InsertSingleAsync(new FileEntry
                {
                    Name = dir.Name,
                    Path = dir.FullName,
                    Extension = "",
                    Size = 0,
                    Modified = dir.LastWriteTime,
                    IsFolder = true
                });

                // Also index files inside the new directory
                await IndexNewDirectoryAsync(dir);
            }
            else if (File.Exists(path))
            {
                var file = new FileInfo(path);
                if (!IsExcludedExtension(file.Extension))
                {
                    await _database.InsertSingleAsync(new FileEntry
                    {
                        Name = file.Name,
                        Path = file.FullName,
                        Extension = file.Extension.TrimStart('.'),
                        Size = file.Length,
                        Modified = file.LastWriteTime,
                        IsFolder = false
                    });
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Index contents of a newly created directory
    /// </summary>
    private async Task IndexNewDirectoryAsync(DirectoryInfo dir)
    {
        try
        {
            // Index files in this directory
            foreach (var file in dir.EnumerateFiles())
            {
                try
                {
                    if (IsExcludedExtension(file.Extension))
                        continue;

                    await _database.InsertSingleAsync(new FileEntry
                    {
                        Name = file.Name,
                        Path = file.FullName,
                        Extension = file.Extension.TrimStart('.'),
                        Size = file.Length,
                        Modified = file.LastWriteTime,
                        IsFolder = false
                    });
                }
                catch { }
            }

            // Recursively index subdirectories
            foreach (var subDir in dir.EnumerateDirectories())
            {
                try
                {
                    if (ShouldIgnore(subDir.FullName))
                        continue;

                    await _database.InsertSingleAsync(new FileEntry
                    {
                        Name = subDir.Name,
                        Path = subDir.FullName,
                        Extension = "",
                        Size = 0,
                        Modified = subDir.LastWriteTime,
                        IsFolder = true
                    });

                    await IndexNewDirectoryAsync(subDir);
                }
                catch { }
            }
        }
        catch { }
    }

    private async Task HandleDeletedAsync(string path)
    {
        await _database.DeleteByPathAsync(path);
    }

    private async Task HandleRenamedAsync(string oldPath, string newPath)
    {
        await _database.UpdatePathAsync(oldPath, newPath);
    }

    private async Task HandleModifiedAsync(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);

                // Only update if file exists in database
                if (await _database.ExistsAsync(path))
                {
                    await _database.UpdateFileAsync(new FileEntry
                    {
                        Name = file.Name,
                        Path = file.FullName,
                        Extension = file.Extension.TrimStart('.'),
                        Size = file.Length,
                        Modified = file.LastWriteTime,
                        IsFolder = false
                    });
                }
                else
                {
                    // File was created but we missed the create event - add it
                    await HandleCreatedAsync(path);
                }
            }
        }
        catch { }
    }

    private bool ShouldIgnore(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;

        // Ignore excluded folders
        foreach (var excluded in _settingsManager.Settings.ExcludedFolders)
        {
            if (path.Contains($"\\{excluded}\\", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith($"\\{excluded}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Ignore temporary/system files
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName)) return true;

        if (fileName.StartsWith("~$") ||
            fileName.StartsWith(".") ||
            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Ignore database files
        if (fileName.StartsWith("AnythingSearch.db", StringComparison.OrdinalIgnoreCase))
        {
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

// Helper classes
public class FileSystemChange
{
    public ChangeType Type { get; set; }
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public DateTime Timestamp { get; set; }
}

public enum ChangeType
{
    Created,
    Deleted,
    Renamed,
    Modified
}