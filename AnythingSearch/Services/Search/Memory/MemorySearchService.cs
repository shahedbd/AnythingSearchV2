using System.Collections.Concurrent;
using AnythingSearch.Helper;
using AnythingSearch.Models;
using Timer = System.Threading.Timer;

namespace AnythingSearch.Services.Search.Memory;

/// <summary>
/// Owns the in-memory search index: builds it, keeps it current, and answers queries from it.
///
/// The snapshot in <see cref="MemoryFileIndex"/> is immutable, so this class layers live
/// file-system changes on top of it as a small delta (entries added since the snapshot, paths
/// removed since the snapshot) and folds that delta into every result. The delta stays tiny
/// because a rebuild is scheduled as soon as it grows or once the disk goes quiet, and rebuilding
/// happens on a background thread against a separate read-only connection - searches keep running
/// on the previous snapshot until the new one is swapped in with a single reference assignment.
///
/// SQLite remains the durable store; this is purely a read accelerator in front of it.
///
/// Split into partial classes: this file owns the snapshot lifecycle, the pending-change delta
/// and the rebuild policy; see MemorySearchService.Query.cs for query execution and how the
/// delta is folded into a result.
/// </summary>
public sealed partial class MemorySearchService : IDisposable
{
    private readonly string _databasePath;
    private readonly Timer _rebuildTimer;

    private volatile MemoryFileIndex? _snapshot;

    /// <summary>
    /// The pending changes, keyed by path: a non-null value is the current state of that path, a
    /// null value means it has been deleted since the snapshot was taken. One map rather than an
    /// added dictionary plus a removed set, because a search needs exactly one question answered
    /// per result row - "does the delta speak for this path?" - and that is one lookup here.
    ///
    /// Concurrent, and read without copying. The file watcher writes to this from a background
    /// thread while searches read it on every keystroke, and the two previous shapes both made
    /// that collision expensive. Rebuilding an immutable copy on every single change made a large
    /// batch of events quadratic; deferring that copy to the next reader instead moved the cost
    /// onto the search box, which then rebuilt a 60,000 entry list and hash set - under the same
    /// lock the watcher was taking thousands of times a second - for every character typed.
    /// Striped locking means a writer now blocks a reader only when they hit the same bucket.
    /// </summary>
    private ConcurrentDictionary<string, FileEntry?> _pending = NewPendingMap();

    /// <summary>The current pending map. Not a volatile field: it is swapped with Interlocked.</summary>
    private ConcurrentDictionary<string, FileEntry?> Pending => Volatile.Read(ref _pending);

    /// <summary>
    /// Size of <see cref="_pending"/>, and how much of it is deletions. Tracked with Interlocked
    /// because ConcurrentDictionary.Count takes every one of the map's internal locks, and this
    /// is consulted on each change and on each search. Approximate under concurrency by design -
    /// it drives a size guard and a status label, neither of which needs an exact figure.
    /// </summary>
    private int _pendingTotal;
    private int _pendingRemoved;

    private int _rebuildInFlight;   // 0 = idle, 1 = building
    private int _rebuildQueued;     // a rebuild was asked for while one was already running
    /// <summary>
    /// When the last rebuild finished, on the Environment.TickCount64 clock. Zero, not
    /// long.MinValue, so the very first churn-driven rebuild sees the machine's uptime as the
    /// elapsed gap and runs immediately, instead of an arithmetic overflow that reads as a
    /// rebuild in the future and holds it back for a cooldown it never earned.
    /// </summary>
    private long _lastRebuildTicks;
    private volatile bool _disposed;

    /// <summary>Rebuild once this many pending changes have accumulated.</summary>
    private const int DeltaRebuildThreshold = 20_000;

    /// <summary>
    /// Stop growing the delta past this. Every search scans the delta, so an unbounded delta makes
    /// searching get slower the busier the disk is - exactly backwards. Past this point the
    /// pending changes are dropped and the snapshot is treated as stale instead: searches stay
    /// fast and the next rebuild picks everything up from the database, which is the durable copy
    /// in any case.
    /// </summary>
    private const int DeltaOverflowLimit = 60_000;

    /// <summary>Rebuild this long after the last change, so a quiet machine stays exact.</summary>
    private const int QuietRebuildDelayMs = 120_000;

    /// <summary>
    /// Minimum gap between rebuilds. A rebuild reads every row in the database, so letting one
    /// start the instant the last finished turned a busy disk into a continuous rebuild loop.
    /// </summary>
    private const int RebuildCooldownMs = 15_000;

    /// <summary>
    /// Minimum gap between compacting collections. One stops every managed thread, so however
    /// cheap it measures, a burst of rebuilds must not be able to chain them - that is exactly
    /// how the previous attempt at reclaiming this memory turned into visible freezes.
    ///
    /// Half the rebuild cooldown's worth of slack either way: rebuilds are themselves spaced by
    /// <see cref="RebuildCooldownMs"/>, so this lets roughly every other one decommit. A rebuild
    /// that lands inside the window leaves the process holding both snapshots - measured at
    /// 171 MB against a 97 MB floor - so the window is also the longest that spike can last, and
    /// there is little reason to make it long when the collection it gates costs about 20 ms.
    /// </summary>
    private const int CompactionCooldownMs = 30_000;

    /// <summary>When the last compacting collection ran, on the Environment.TickCount64 clock.</summary>
    private long _lastCompactionTicks;

    public event Action<string>? StatusChanged;

    /// <summary>Raised the first time a snapshot becomes available, and after every rebuild.</summary>
    public event Action? SnapshotReady;

    private readonly TaskCompletionSource _firstSnapshot =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once a snapshot has been loaded (successfully or not). Database maintenance
    /// waits on this: VACUUM needs the file to itself, and it would otherwise be competing with
    /// the read-only connection the first snapshot is being built from.
    /// </summary>
    public Task FirstSnapshotSettled => _firstSnapshot.Task;

    public MemorySearchService(string databasePath)
    {
        _databasePath = databasePath;
        _rebuildTimer = new Timer(_ => RequestRebuild("periodic refresh"), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static ConcurrentDictionary<string, FileEntry?> NewPendingMap() =>
        new(Environment.ProcessorCount * 2, 1024, StringComparer.OrdinalIgnoreCase);

    public bool IsReady => _snapshot != null;

    /// <summary>
    /// Number of indexed entries, counting the pending delta. Approximate between rebuilds: a
    /// path that was already in the snapshot and has since been rewritten is counted twice, so
    /// the figure can run a little high until the next rebuild settles it.
    /// </summary>
    public long Count
    {
        get
        {
            var snapshot = _snapshot;
            if (snapshot == null) return 0;

            int removed = Volatile.Read(ref _pendingRemoved);
            int added = Volatile.Read(ref _pendingTotal) - removed;
            return snapshot.Count + added - removed;
        }
    }

    public long ApproximateBytes => _snapshot?.ApproximateBytes ?? 0;

    /// <summary>
    /// Start building the first snapshot. Returns as soon as the work is queued; callers keep
    /// using the SQLite path until <see cref="SnapshotReady"/> fires.
    /// </summary>
    public void Start() => RequestRebuild("initial load");

    #region Live updates

    /// <summary>Record an entry the file watcher has just written to SQLite.</summary>
    public void NotifyAdded(FileEntry entry) => Mutate(entry.Path, entry);

    /// <summary>Record a path the file watcher has just removed from SQLite.</summary>
    public void NotifyRemoved(string path) => Mutate(path, null);

    /// <summary>Record a path whose metadata changed - the newer entry simply replaces it.</summary>
    public void NotifyUpdated(FileEntry entry) => Mutate(entry.Path, entry);

    /// <summary>
    /// Record one change. <paramref name="entry"/> is the current state of the path, or null when
    /// it no longer exists. Constant time, and it takes no lock a search could be waiting on.
    /// </summary>
    private void Mutate(string path, FileEntry? entry)
    {
        if (_disposed || _snapshot == null) return;

        var pending = Pending;
        int total;

        if (pending.TryAdd(path, entry))
        {
            total = Interlocked.Increment(ref _pendingTotal);
            if (entry == null) Interlocked.Increment(ref _pendingRemoved);
        }
        else
        {
            pending.TryGetValue(path, out var previous);
            pending[path] = entry;
            total = Volatile.Read(ref _pendingTotal);

            if (previous == null && entry != null) Interlocked.Decrement(ref _pendingRemoved);
            else if (previous != null && entry == null) Interlocked.Increment(ref _pendingRemoved);
        }

        if (total > DeltaOverflowLimit)
        {
            // Too many changes to keep carrying. Drop them and let the rebuild read the current
            // state from the database, rather than making every search pay for them. The map is
            // replaced rather than cleared so a search already walking the old one is unaffected.
            //
            // Cooldown respected: a sustained copy or install refills the delta every few seconds,
            // and starting a full rebuild each time it does means reading the entire database over
            // and over while the user is trying to search.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _pending, NewPendingMap(), pending), pending))
            {
                Interlocked.Exchange(ref _pendingTotal, 0);
                Interlocked.Exchange(ref _pendingRemoved, 0);
                RequestRebuild("more changes than the overlay can carry", respectCooldown: true);
            }
        }
        else if (total >= DeltaRebuildThreshold)
        {
            RequestRebuild("delta threshold reached", respectCooldown: true);
        }
        else if (total <= 2 || (total & 0xFF) == 0)
        {
            // Throttled: indexing one new directory can raise thousands of changes, and
            // rescheduling the timer for every one of them contends on the shared timer queue for
            // no benefit. Pushing the deadline out less often only ever makes the rebuild happen
            // sooner, never later, so nothing is lost.
            _rebuildTimer.Change(QuietRebuildDelayMs, Timeout.Infinite);
        }
    }

    #endregion

    #region Rebuild

    /// <summary>Queue a snapshot rebuild unless one is already running.</summary>
    /// <param name="respectCooldown">
    /// True for rebuilds driven by file-system churn, which arrive as fast as the disk is busy.
    /// Those wait out <see cref="RebuildCooldownMs"/> since the last one; the handful of rebuilds
    /// that make the app usable at all - the initial load, a phase publishing - do not.
    /// </param>
    public void RequestRebuild(string reason, bool respectCooldown = false)
    {
        if (_disposed) return;

        if (respectCooldown)
        {
            long since = Environment.TickCount64 - Volatile.Read(ref _lastRebuildTicks);
            if (since < RebuildCooldownMs)
            {
                _rebuildTimer.Change((int)(RebuildCooldownMs - since), Timeout.Infinite);
                return;
            }
        }

        if (Interlocked.CompareExchange(ref _rebuildInFlight, 1, 0) != 0)
        {
            // Remember it instead of dropping it. Phased indexing publishes one drive after
            // another, so a request landing mid-rebuild is normal - and losing it would leave
            // that drive out of the snapshot until something else happened to trigger a rebuild.
            Interlocked.Exchange(ref _rebuildQueued, 1);
            return;
        }

        _rebuildTimer.Change(Timeout.Infinite, Timeout.Infinite);

        Task.Run(() =>
        {
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Everything queued before this point is captured by the rebuild, so it can be
                // dropped once the new snapshot is published. Changes that arrive DURING the
                // rebuild are kept: they may or may not be in the snapshot, and keeping a
                // redundant one is harmless while losing one is not. The entries are compared by
                // reference so a path written again mid-rebuild keeps its newer entry.
                var consumedFrom = Pending;
                var consumed = consumedFrom.ToArray();

                var index = MemoryIndexBuilder.Build(_databasePath, null, CancellationToken.None);

                // Skipped when the map was replaced meanwhile (an overflow): everything in the
                // captured copy went with it, so there is nothing left to retire.
                if (ReferenceEquals(Pending, consumedFrom))
                {
                    foreach (var entry in consumed)
                    {
                        if (!consumedFrom.TryRemove(entry)) continue;

                        Interlocked.Decrement(ref _pendingTotal);
                        if (entry.Value == null) Interlocked.Decrement(ref _pendingRemoved);
                    }
                }

                _snapshot = index;
                stopwatch.Stop();

                ReclaimAfterRebuild();

                StatusChanged?.Invoke(
                    $"Instant search ready - {index.Count:N0} items in {index.ApproximateBytes / (1024 * 1024):N0} MB " +
                    $"({stopwatch.ElapsedMilliseconds:N0} ms, {reason})");
                SnapshotReady?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Log($"In-memory index build failed ({reason}): {ex}");
                StatusChanged?.Invoke($"In-memory index unavailable ({ex.Message}) - using the database instead");
            }
            finally
            {
                _firstSnapshot.TrySetResult();

                // Stamped before the flag is cleared, so the cooldown covers the gap.
                Volatile.Write(ref _lastRebuildTicks, Environment.TickCount64);
                Volatile.Write(ref _rebuildInFlight, 0);

                // Hand a queued request to the timer rather than starting it straight away. Going
                // directly back into RequestRebuild meant a disk busy enough to keep the queue
                // full produced one rebuild after another with no gap, and every one of them
                // competed with the searches the user was waiting on.
                if (Interlocked.Exchange(ref _rebuildQueued, 0) == 1 && !_disposed)
                    _rebuildTimer.Change(RebuildCooldownMs, Timeout.Infinite);
            }
        });
    }

    /// <summary>
    /// Hand back the memory a rebuild leaves behind.
    ///
    /// A rebuild drops the previous snapshot - on a 1.4 million entry index that is about 90 MB,
    /// in arrays every one of which is far past the 85 KB large object heap threshold. Freeing
    /// them is not enough on its own, twice over: an ordinary collection does not compact the
    /// LOH, so the space stays committed as holes, and even a compacting collection leaves the
    /// emptied regions committed for reuse rather than returning them to the operating system.
    ///
    /// Measured across five rebuilds of that index, with the live snapshot at 87.7 MB throughout:
    ///
    ///   background collection (what this used to do)   330 MB private, 115 MB reported fragmented
    ///   forced + CompactOnce                           186 MB private, 175 MB committed, 14-20 ms
    ///   aggressive                                     ~100 MB private, 87.7 MB committed, 20-25 ms
    ///
    /// Only the aggressive mode decommits, which is why it is the one used here, and it lands the
    /// process within a few megabytes of the index it is actually holding.
    ///
    /// That is a deliberate return to a mode this code previously backed away from, so it is worth
    /// being precise about what went wrong before. The earlier version asked for the same
    /// collection on EVERY rebuild with nothing spacing them out - and a rebuild is itself a
    /// 1.4 second read of every row in the database, so file-system churn produced a continuous
    /// train of rebuilds that each ended in a stop-the-world pause. The pauses were a symptom of
    /// the rebuild loop, not the cost of the collection: timed on its own it is 20-25 ms.
    ///
    /// The cooldown below is what keeps it that way. A rebuild landing inside the window still
    /// asks for a background collection, which retires the old snapshot without stopping the
    /// thread a search is running on; the next one past the window decommits.
    /// </summary>
    private void ReclaimAfterRebuild()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastCompactionTicks) < CompactionCooldownMs)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: false);
            return;
        }

        Volatile.Write(ref _lastCompactionTicks, now);
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>Drop the snapshot, e.g. while the database is being rebuilt from scratch.</summary>
    public void Invalidate()
    {
        _snapshot = null;
        Volatile.Write(ref _pending, NewPendingMap());
        Interlocked.Exchange(ref _pendingTotal, 0);
        Interlocked.Exchange(ref _pendingRemoved, 0);
    }

    #endregion

    public void Dispose()
    {
        _disposed = true;
        _rebuildTimer.Dispose();
        _snapshot = null;
    }
}
