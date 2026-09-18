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
///
/// <see cref="QueueChange"/> runs on the FileSystemWatcher callback thread, which has a hard
/// deadline: events Windows cannot hand over are dropped from a fixed kernel buffer and reported
/// as an overflow. So it does no allocation it can avoid, and nothing here calls
/// ConcurrentDictionary.Count - that property takes every one of the dictionary's internal locks,
/// which on the one path every file-system event on the machine goes through turns the queue into
/// the bottleneck it exists to prevent. <see cref="_pendingCount"/> tracks the size instead.
/// </summary>
public partial class FileWatcherService
{
    /// <summary>
    /// Size of <see cref="_pendingChanges"/>, maintained with Interlocked. See the class remarks
    /// for why the dictionary's own Count is not used on this path.
    /// </summary>
    private int _pendingCount;

    /// <summary>
    /// Set while a high-water-mark drain has been scheduled but not yet claimed. Without it every
    /// single event arriving above the mark queued another thread-pool item, and all but one of
    /// them existed only to lose the race for the batch.
    /// </summary>
    private int _drainScheduled;

    /// <summary>
    /// Queue a change for processing with debouncing
    /// </summary>
    private void QueueChange(string path, ChangeType type, string? oldPath = null)
    {
        if (!_isRunning) return;
        if (ShouldIgnore(path)) return;

        // UtcNow, not Now: these stamps are only ever compared with each other, and Now adds a
        // time-zone conversion to every event on a path that handles every change on the machine.
        var now = DateTime.UtcNow;

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
        //
        // Spelled out as TryGetValue/TryUpdate/TryAdd rather than AddOrUpdate so that every change
        // in the queue's SIZE goes through exactly one atomic operation the counter can be paired
        // with. AddOrUpdate can run its add factory and then discard the result after losing a
        // race, which would leave _pendingCount drifting further from the truth with every event.
        int pending;
        while (true)
        {
            if (_pendingChanges.TryGetValue(path, out var existing))
            {
                // A plain Modified must not erase a queued Created/Deleted/Renamed for the same path
                if (type == ChangeType.Modified && existing.Type != ChangeType.Modified)
                {
                    existing.Timestamp = now;
                    pending = Volatile.Read(ref _pendingCount);
                    break;
                }

                change.FirstSeen = existing.FirstSeen;

                // Replaced or flushed since the read - look again rather than overwrite blindly.
                if (!_pendingChanges.TryUpdate(path, change, existing)) continue;

                pending = Volatile.Read(ref _pendingCount);
                break;
            }

            if (_pendingChanges.TryAdd(path, change))
            {
                pending = Interlocked.Increment(ref _pendingCount);
                break;
            }
        }

        // Drain as soon as the queue is filling up, so we never have to drop a change
        if (pending >= HighWaterMark && Interlocked.CompareExchange(ref _drainScheduled, 1, 0) == 0)
        {
            bool flushAll = pending >= MaxPendingChanges;
            Task.Run(async () =>
            {
                try { await ProcessChangesAsync(flushAll); }
                finally { Volatile.Write(ref _drainScheduled, 0); }
            });
        }
    }

    /// <summary>
    /// Process queued changes (called by timer)
    /// </summary>
    private void ProcessChangesCallback(object? state)
    {
        // Cheap early exit; ProcessChangesAsync claims the batch for real.
        if (Volatile.Read(ref _processing) != 0) return;

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
        // Refused once shutdown has begun. This is the single place a batch is claimed, so it is
        // also the single place that has to say no - otherwise a batch could start writing to the
        // database moments before the owner disposes it.
        if (_stopping) return;
        if (_pendingChanges.IsEmpty && _deferredDirectories.Count == 0) return;

        // Claim the batch atomically. The check-then-set this replaced could let the 3-second
        // timer and the high-water-mark drain both get past it, so two batches applied the same
        // queued changes at once - duplicated database work, and twice the entries pushed into
        // the in-memory overlay.
        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return;

        try
        {
            // Everything one batch is allowed to index from directory walks, shared between the
            // backlog below and whatever the batch's own changes turn up. This is the cap that
            // keeps a single notification from monopolising the database connection searches use.
            ResetWalkBudget();

            var sortedChanges = TakeBatch(flushAll);

            // Nothing new, but an earlier batch may still owe work.
            if (sortedChanges.Count == 0 && _deferredDirectories.Count == 0)
                return;

            var (processed, errors) = await ApplyChangesAsync(sortedChanges);

            if (processed > 0 || errors > 0)
            {
                var message = errors > 0
                    ? $"Auto-watch: {processed} change(s), {errors} error(s)"
                    : $"Auto-watch: {processed} change(s) synced";
                StatusChanged?.Invoke(message);
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Auto-watch: Error processing changes: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _processing, 0);
        }
    }

    /// <summary>
    /// Remove the changes that are ready to apply and return them in the order they must be
    /// applied: deletes, then creates, then renames, then modifications.
    /// </summary>
    private List<FileSystemChange> TakeBatch(bool flushAll)
    {
        // Take everything that has been quiet long enough, plus anything that has been
        // waiting too long overall (a continuously written file never goes quiet).
        var now = DateTime.UtcNow;
        var quietCutoff = now.AddMilliseconds(-DebounceMs);
        var ageCutoff = now.AddMilliseconds(-MaxChangeAgeMs);

        var batch = new List<FileSystemChange>();
        foreach (var kvp in _pendingChanges)
        {
            var change = kvp.Value;
            if (!flushAll && change.Timestamp >= quietCutoff && change.FirstSeen >= ageCutoff) continue;

            if (_pendingChanges.TryRemove(kvp))
            {
                Interlocked.Decrement(ref _pendingCount);
                batch.Add(change);
            }
        }

        // Sorted in place rather than through OrderBy: this runs on every batch and the old
        // Select/Where/ToList/OrderBy/ToList chain built three copies of it to no purpose.
        batch.Sort(static (a, b) => Rank(a.Type).CompareTo(Rank(b.Type)));
        return batch;
    }

    private static int Rank(ChangeType type) => type switch
    {
        ChangeType.Deleted => 0,
        ChangeType.Created => 1,
        ChangeType.Renamed => 2,
        ChangeType.Modified => 3,
        _ => 4
    };

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
            // Before the batch's own changes, not after: a backlog that is always queued behind
            // whatever arrived in the last three seconds on a busy machine never drains at all.
            await DrainDeferredDirectoriesAsync();

            foreach (var change in sortedChanges)
            {
                if (_stopping) break;

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
                    Helper.Logger.Log($"File watcher could not apply a change to {change.Path}: {ex.Message}");
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
