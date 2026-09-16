using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Debounce/batch queue side of FileWatcherService: buffers raw OS events, deduplicates them
/// per path, and periodically flushes a batch to the sync handlers in
/// FileWatcherService.SyncHandlers.cs.
///
/// Two rules keep the queue from silently losing changes:
/// 1. A queued path is flushed once it has been quiet for DebounceMs OR once it has been waiting
///    for MaxChangeAgeMs - a path that keeps receiving events can never starve the queue.
/// 2. When the queue is full it is drained immediately (ignoring the debounce) instead of
///    dropping the incoming change.
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

        var now = DateTime.Now;

        var change = new FileSystemChange
        {
            Type = type,
            Path = path,
            OldPath = oldPath,
            Timestamp = now,
            FirstSeen = now
        };

        // Use path as key for automatic deduplication.
        // The newest event wins: it describes the current state of the path. Keeping an older
        // "Deleted" over a newer "Created" used to drop files that are saved atomically
        // (write temp -> delete target -> rename), which is how most editors and git write files.
        _pendingChanges.AddOrUpdate(path, change, (key, existing) =>
        {
            // A plain Modified must not erase a queued Created/Deleted/Renamed for the same path
            if (type == ChangeType.Modified && existing.Type != ChangeType.Modified)
            {
                existing.Timestamp = now;
                return existing;
            }

            change.FirstSeen = existing.FirstSeen;
            return change;
        });

        // Drain as soon as the queue is filling up, so we never have to drop a change
        if (_pendingChanges.Count >= HighWaterMark && !_isProcessing)
        {
            Task.Run(async () => await ProcessChangesAsync(_pendingChanges.Count >= MaxPendingChanges));
        }
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
    /// Process all pending changes.
    /// </summary>
    /// <param name="flushAll">
    /// Ignore the debounce window and flush everything - used when the queue is at capacity.
    /// </param>
    private async Task ProcessChangesAsync(bool flushAll = false)
    {
        if (_isProcessing || _pendingChanges.IsEmpty) return;

        _isProcessing = true;

        try
        {
            // Take everything that has been quiet long enough, plus anything that has been
            // waiting too long overall (a continuously written file never goes quiet).
            var now = DateTime.Now;
            var quietCutoff = now.AddMilliseconds(-DebounceMs);
            var ageCutoff = now.AddMilliseconds(-MaxChangeAgeMs);

            var changesToProcess = _pendingChanges
                .Select(kvp => kvp.Value)
                .Where(c => flushAll || c.Timestamp < quietCutoff || c.FirstSeen < ageCutoff)
                .ToList();

            if (changesToProcess.Count == 0)
                return;

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

            var (processed, errors) = await ApplyChangesAsync(sortedChanges);

            if (processed > 0 || errors > 0)
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

    /// <summary>
    /// Apply a sorted batch inside a single transaction instead of one implicit SQLite
    /// transaction per statement (faster and atomic per batch).
    /// </summary>
    private async Task<(int Processed, int Errors)> ApplyChangesAsync(List<FileSystemChange> sortedChanges)
    {
        int processed = 0;
        int errors = 0;

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
                            break;

                        case ChangeType.Deleted:
                            await HandleDeletedAsync(change.Path);
                            break;

                        case ChangeType.Renamed:
                            if (string.IsNullOrEmpty(change.OldPath)) continue;
                            await HandleRenamedAsync(change.OldPath, change.Path);
                            break;

                        case ChangeType.Modified:
                            await HandleModifiedAsync(change.Path);
                            break;
                    }

                    processed++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"FileWatcher error ({change.Path}): {ex.Message}");
                    errors++;
                }
            }
        }
        finally
        {
            await _database.CommitIncrementalTransactionAsync();
        }

        return (processed, errors);
    }
}
