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
/// </summary>
public sealed class MemorySearchService : IDisposable
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
    private volatile bool _disposed;

    /// <summary>Rebuild once this many pending changes have accumulated.</summary>
    private const int DeltaRebuildThreshold = 20_000;

    /// <summary>Rebuild this long after the last change, so a quiet machine stays exact.</summary>
    private const int QuietRebuildDelayMs = 120_000;

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

    /// <summary>
    /// Run a query against the snapshot. Returns false when no snapshot exists yet, which tells
    /// the caller to fall back to SQLite.
    /// </summary>
    public bool TrySearch(
        string query,
        int limit,
        CancellationToken cancellationToken,
        out List<FileEntry> results,
        out int totalMatches)
    {
        var snapshot = _snapshot;
        if (snapshot == null)
        {
            results = new List<FileEntry>();
            totalMatches = 0;
            return false;
        }

        var indices = snapshot.Search(query, limit, cancellationToken, out totalMatches);
        var delta = PublishDelta();

        results = new List<FileEntry>(indices.Count);
        foreach (var index in indices)
        {
            // Drop the snapshot's version of any path the delta speaks for - deleted since, or
            // written again since. MergeAdded puts the current version back for the latter.
            // The total can be off by the few such rows that fall outside this page, which is not
            // something a result count of thousands makes visible.
            if (delta.Shadowed.Count > 0 && delta.Shadowed.Contains(snapshot.PathOf(index)))
            {
                totalMatches--;
                continue;
            }

            results.Add(snapshot.Materialize(index));
        }

        if (delta.Added.Count > 0)
            MergeAdded(query, limit, delta, results, ref totalMatches);

        return true;
    }

    /// <summary>
    /// Fold entries created since the snapshot into the result list. The delta is small by design,
    /// so a straight scan and a re-sort of the merged page is cheaper than any index over it.
    /// </summary>
    private void MergeAdded(string query, int limit, Delta delta, List<FileEntry> results, ref int totalMatches)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int added = 0;

        foreach (var entry in delta.Added)
        {
            if (!MatchesAllTerms(entry, terms)) continue;

            results.Add(entry);
            added++;
        }

        if (added == 0) return;

        totalMatches += added;

        var pattern = terms[0];
        results.Sort((a, b) => Compare(a, b, pattern));

        if (results.Count > limit)
            results.RemoveRange(limit, results.Count - limit);
    }

    private static bool MatchesAllTerms(FileEntry entry, string[] terms)
    {
        foreach (var term in terms)
        {
            if (entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Path.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            return false;
        }
        return true;
    }

    /// <summary>The ordering the SQLite query used: folders, then relevance, then shortest name.</summary>
    private static int Compare(FileEntry a, FileEntry b, string pattern)
    {
        int byFolder = b.IsFolder.CompareTo(a.IsFolder);
        if (byFolder != 0) return byFolder;

        int byRelevance = Relevance(a, pattern).CompareTo(Relevance(b, pattern));
        if (byRelevance != 0) return byRelevance;

        int byLength = a.Name.Length.CompareTo(b.Name.Length);
        if (byLength != 0) return byLength;

        return string.CompareOrdinal(a.Name, b.Name);
    }

    private static int Relevance(FileEntry entry, string pattern)
    {
        if (entry.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return 1;
        if (entry.Name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) return 2;
        if (entry.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

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

            _publishedDelta = null;   // readers rebuild it on their next search
            pending = _pendingAdded.Count + _pendingRemoved.Count;
        }

        if (pending >= DeltaRebuildThreshold)
            RequestRebuild("delta threshold reached");
        else
            _rebuildTimer.Change(QuietRebuildDelayMs, Timeout.Infinite);
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
        if (Interlocked.CompareExchange(ref _rebuildInFlight, 1, 0) != 0) return;

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
                // a rebuild drops the previous snapshot outright. Both land on the large object
                // heap, which is not collected on its own schedule - without this the process
                // keeps holding roughly two indexes' worth of memory. Runs once per rebuild,
                // never on the search path.
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

                StatusChanged?.Invoke(
                    $"Instant search ready - {index.Count:N0} items in {index.ApproximateBytes / (1024 * 1024):N0} MB " +
                    $"({stopwatch.ElapsedMilliseconds:N0} ms, {reason})");
                SnapshotReady?.Invoke();
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"In-memory index unavailable ({ex.Message}) - using the database instead");
            }
            finally
            {
                _firstSnapshot.TrySetResult();
                Volatile.Write(ref _rebuildInFlight, 0);
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
