using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Debounce/batch queue side of FileWatcherService: buffers raw OS events, deduplicates them
/// per path, and periodically flushes a batch to the sync handlers in
/// FileWatcherService.SyncHandlers.cs.
/// </summary>
public partial class FileWatcherService
{
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

        // Drain early once the queue is getting full, instead of waiting for the next
        // scheduled tick, so we're less likely to ever hit MaxPendingChanges and start dropping changes.
        if (_pendingChanges.Count >= HighWaterMark && !_isProcessing)
        {
            Task.Run(async () => await ProcessChangesAsync());
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

            // Process the whole batch inside a single transaction instead of one
            // implicit SQLite transaction per statement (faster and atomic per batch).
            await _database.BeginIncrementalTransactionAsync();
            try
            {
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
            }
            finally
            {
                await _database.CommitIncrementalTransactionAsync();
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
}
