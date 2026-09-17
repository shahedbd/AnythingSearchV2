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

    // Pending changes are accumulated in ordinary mutable collections under _deltaLock, and an
    // immutable copy is published for readers only when one is actually asked for. Rebuilding
    // that copy on every single change made a large batch of file-system events - a branch
    // checkout, an installer unpacking - quadratic in the size of the batch.
    private readonly Dictionary<string, FileEntry> _pendingAdded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingRemoved = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _deltaLock = new();
    private volatile Delta? _publishedDelta = Delta.Empty;

    private int _rebuildInFlight;   // 0 = idle, 1 = building
    private int _rebuildQueued;     // a rebuild was asked for while one was already running
    private volatile bool _disposed;

    /// <summary>Rebuild once this many pending changes have accumulated.</summary>
    private const int DeltaRebuildThreshold = 20_000;

    /// <summary>
    /// Stop growing the delta past this. Every search copies the delta and scans it, so an
    /// unbounded delta makes searching get slower the busier the disk is - exactly backwards.
    /// Past this point the pending changes are dropped and the snapshot is treated as stale
    /// instead: searches stay fast and the next rebuild picks everything up from the database,
    /// which is the durable copy in any case.
    /// </summary>
    private const int DeltaOverflowLimit = 60_000;

    /// <summary>Rebuild this long after the last change, so a quiet machine stays exact.</summary>
    private const int QuietRebuildDelayMs = 120_000;

    /// <summary>
    /// Minimum gap between rebuilds. A rebuild reads every row in the database, so letting one
    /// start the instant the last finished turned a busy disk into a continuous rebuild loop.
    /// </summary>
    private const int RebuildCooldownMs = 15_000;

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

            lock (_deltaLock)
                return snapshot.Count + _pendingAdded.Count - _pendingRemoved.Count;
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
    /// it no longer exists. Constant time: the immutable view readers use is built later, once.
    /// </summary>
    private void Mutate(string path, FileEntry? entry)
    {
        if (_disposed || _snapshot == null) return;

        int pending;
        bool overflowed;
        lock (_deltaLock)
        {
            if (entry == null)
            {
                _pendingAdded.Remove(path);
                _pendingRemoved.Add(path);
            }
            else
            {
                _pendingRemoved.Remove(path);
                _pendingAdded[path] = entry;
            }

            pending = _pendingAdded.Count + _pendingRemoved.Count;
            overflowed = pending > DeltaOverflowLimit;

            if (overflowed)
            {
                // Too many changes to keep carrying. Drop them and let the rebuild read the
                // current state from the database, rather than making every search pay for them.
                _pendingAdded.Clear();
                _pendingRemoved.Clear();
                pending = 0;
            }

            _publishedDelta = null;   // readers rebuild it on their next search
        }

        if (overflowed)
            RequestRebuild("more changes than the overlay can carry");
        else if (pending >= DeltaRebuildThreshold)
            RequestRebuild("delta threshold reached");
        else if (pending <= 2 || (pending & 0xFF) == 0)
        {
            // Throttled: indexing one new directory can raise thousands of changes, and
            // rescheduling the timer for every one of them contends on the shared timer queue for
            // no benefit. Pushing the deadline out less often only ever makes the rebuild happen
            // sooner, never later, so nothing is lost.
            _rebuildTimer.Change(QuietRebuildDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// The immutable view of the pending changes, rebuilt only after something has changed. A
    /// search never mutates it, so it can be read without holding the lock once published.
    /// </summary>
    private Delta PublishDelta()
    {
        var published = _publishedDelta;
        if (published != null) return published;

        lock (_deltaLock)
        {
            if (_publishedDelta == null)
            {
                // Shadowed = paths the snapshot must not answer for. That is everything deleted
                // since the snapshot AND everything re-added since: a file the snapshot already
                // knows about but that has been written again must be shown from the delta, with
                // its current size and timestamp, rather than listed twice.
                var shadowed = new HashSet<string>(_pendingRemoved, StringComparer.OrdinalIgnoreCase);
                foreach (var path in _pendingAdded.Keys) shadowed.Add(path);

                _publishedDelta = new Delta(new List<FileEntry>(_pendingAdded.Values), shadowed);
            }

            return _publishedDelta;
        }
    }

    #endregion

    #region Rebuild

    /// <summary>Queue a snapshot rebuild unless one is already running.</summary>
    public void RequestRebuild(string reason)
    {
        if (_disposed) return;

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
                Dictionary<string, FileEntry> consumedAdded;
                HashSet<string> consumedRemoved;
                lock (_deltaLock)
                {
                    consumedAdded = new Dictionary<string, FileEntry>(_pendingAdded, StringComparer.OrdinalIgnoreCase);
                    consumedRemoved = new HashSet<string>(_pendingRemoved, StringComparer.OrdinalIgnoreCase);
                }

                var index = MemoryIndexBuilder.Build(_databasePath, null, CancellationToken.None);

                lock (_deltaLock)
                {
                    foreach (var consumed in consumedAdded)
                    {
                        if (_pendingAdded.TryGetValue(consumed.Key, out var current)
                            && ReferenceEquals(current, consumed.Value))
                        {
                            _pendingAdded.Remove(consumed.Key);
                        }
                    }

                    foreach (var path in consumedRemoved) _pendingRemoved.Remove(path);
                    _publishedDelta = null;
                }

                _snapshot = index;
                stopwatch.Stop();

                // Building a snapshot allocates large arrays that are grown and then trimmed, and
                // a rebuild drops the previous snapshot outright, so prompting a collection here
                // keeps the process from holding roughly two indexes' worth of memory.
                //
                // It must be a BACKGROUND collection. This used to ask for
                // GCCollectionMode.Aggressive with blocking: true, compacting: true and
                // LargeObjectHeapCompactionMode.CompactOnce, which suspends every managed thread
                // while it compacts a large object heap holding the whole index. One of those is
                // merely slow; back to back, driven by file-system churn, they froze searches for
                // over a minute. A background collection reclaims nearly as much and never stops
                // the thread a search is running on.
                GC.Collect(2, GCCollectionMode.Forced, blocking: false);

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

    /// <summary>Drop the snapshot, e.g. while the database is being rebuilt from scratch.</summary>
    public void Invalidate()
    {
        _snapshot = null;
        lock (_deltaLock)
        {
            _pendingAdded.Clear();
            _pendingRemoved.Clear();
            _publishedDelta = Delta.Empty;
        }
    }

    #endregion

    public void Dispose()
    {
        _disposed = true;
        _rebuildTimer.Dispose();
        _snapshot = null;
    }

    /// <summary>
    /// Immutable view of the changes not yet folded into the snapshot. Published by
    /// <see cref="PublishDelta"/> and never modified afterwards, so searches read it without a
    /// lock.
    /// </summary>
    private sealed class Delta
    {
        public static readonly Delta Empty = new(
            new List<FileEntry>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        /// <summary>Entries the delta answers for: created or rewritten since the snapshot.</summary>
        public List<FileEntry> Added { get; }

        /// <summary>
        /// Paths the snapshot must stay quiet about - every deleted path, plus every path in
        /// <see cref="Added"/>, so a file the snapshot already knows about is shown once, from
        /// the delta, with its current metadata.
        /// </summary>
        public HashSet<string> Shadowed { get; }

        public Delta(List<FileEntry> added, HashSet<string> shadowed)
        {
            Added = added;
            Shadowed = shadowed;
        }
    }
}
