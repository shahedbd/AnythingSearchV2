using AnythingSearch.Models;

namespace AnythingSearch.Services.Search.Memory;

/// <summary>
/// An immutable, in-memory snapshot of the whole file index - the reason search is now
/// instantaneous instead of taking over a second.
///
/// Why this exists: <c>WHERE Name LIKE '%term%'</c> can never use a B-tree index, so the previous
/// SQLite query read all 3.3 million rows (and joined them to the folder table) off a 430 MB file
/// on every keystroke. EXPLAIN QUERY PLAN reported "SCAN fo / SEARCH f USING INDEX / USE TEMP
/// B-TREE FOR ORDER BY" - three full-dataset operations per character typed.
///
/// This class replaces that with the approach Everything uses: keep the names in RAM and scan
/// them. The names live in one contiguous, case-folded byte blob (see
/// <see cref="PackedStringTable"/>), so a query becomes a vectorised memory scan split across all
/// CPU cores, and ranking uses a bounded heap instead of sorting every match.
///
/// The snapshot is immutable: the writer builds a new one and publishes it with a single
/// reference assignment, so searches never take a lock and never see a half-built index.
/// Live file-system changes are layered on top by <see cref="MemorySearchService"/>.
/// </summary>
public sealed class MemoryFileIndex
{
    /// <summary>Ticks at 2000-01-01, the epoch for the packed <see cref="_modified"/> column.</summary>
    private static readonly long EpochTicks = new DateTime(2000, 1, 1).Ticks;

    private readonly PackedStringTable _names;
    private readonly PackedStringTable _folders;
    private readonly int[] _folderOf;      // file index -> folder table index
    private readonly long[] _sizes;
    private readonly int[] _modified;      // seconds since EpochTicks
    private readonly ulong[] _isFolderBits;

    // Compressed folder -> files mapping (CSR layout). Lets a folder-path match expand straight to
    // the files underneath it instead of re-scanning all 3.3 million entries.
    private readonly int[] _folderFileStart; // length _folders.Count + 1
    private readonly int[] _folderFileList;  // length Count, file indices grouped by folder

    public int Count { get; }
    public int FolderCount => _folders.Count;

    /// <summary>Approximate managed bytes held by this snapshot, for the diagnostics label.</summary>
    public long ApproximateBytes =>
        _names.ApproximateBytes + _folders.ApproximateBytes
        + _folderOf.LongLength * 4L + _sizes.LongLength * 8L + _modified.LongLength * 4L
        + _isFolderBits.LongLength * 8L
        + _folderFileStart.LongLength * 4L + _folderFileList.LongLength * 4L;

    internal MemoryFileIndex(
        PackedStringTable names,
        PackedStringTable folders,
        int[] folderOf,
        long[] sizes,
        int[] modified,
        ulong[] isFolderBits,
        int count)
    {
        _names = names;
        _folders = folders;
        _folderOf = folderOf;
        _sizes = sizes;
        _modified = modified;
        _isFolderBits = isFolderBits;
        Count = count;

        (_folderFileStart, _folderFileList) = BuildFolderMap(folderOf, count, folders.Count);
    }

    private static (int[] Start, int[] List) BuildFolderMap(int[] folderOf, int count, int folderCount)
    {
        var start = new int[folderCount + 1];
        for (int i = 0; i < count; i++)
            start[folderOf[i] + 1]++;

        for (int f = 0; f < folderCount; f++)
            start[f + 1] += start[f];

        var list = new int[count];
        var cursor = (int[])start.Clone();
        for (int i = 0; i < count; i++)
            list[cursor[folderOf[i]]++] = i;

        return (start, list);
    }

    internal static int PackModified(DateTime value)
    {
        long seconds = (value.Ticks - EpochTicks) / TimeSpan.TicksPerSecond;
        return seconds < int.MinValue ? int.MinValue
             : seconds > int.MaxValue ? int.MaxValue
             : (int)seconds;
    }

    private static DateTime UnpackModified(int seconds) =>
        new DateTime(EpochTicks + seconds * TimeSpan.TicksPerSecond);

    public bool IsFolder(int index) => (_isFolderBits[index >> 6] & (1UL << (index & 63))) != 0;

    /// <summary>Full path of one entry, rebuilt from the folder table and the name table.</summary>
    public string PathOf(int index) => _folders.Original(_folderOf[index]) + "\\" + _names.Original(index);

    public FileEntry Materialize(int index)
    {
        var name = _names.Original(index);
        var isFolder = IsFolder(index);
        return new FileEntry
        {
            Name = name,
            Path = _folders.Original(_folderOf[index]) + "\\" + name,
            Extension = isFolder ? "" : System.IO.Path.GetExtension(name).TrimStart('.'),
            Size = _sizes[index],
            Modified = UnpackModified(_modified[index]),
            IsFolder = isFolder
        };
    }

    #region Search

    /// <summary>
    /// Search names and folder paths for every space-separated term (AND), ranked exactly the way
    /// the SQLite query used to rank: folders first, then relevance, then shortest name, then name.
    /// </summary>
    /// <param name="totalMatches">
    /// The real number of matches, which is normally larger than the returned list - the caller
    /// only ever displays <paramref name="limit"/> rows.
    /// </param>
    /// <remarks>
    /// Cancellation does NOT throw. Every keystroke supersedes the search before it, so on this
    /// path cancellation is the normal case rather than an exceptional one - throwing meant
    /// building and unwinding an exception through several frames for every character typed, and
    /// it stopped the debugger on a condition that was never a fault. A cancelled search abandons
    /// its scan and returns an empty result instead; callers already discard the result of a
    /// search they cancelled, so nothing downstream has to change.
    /// </remarks>
    public List<int> Search(string query, int limit, CancellationToken cancellationToken, out int totalMatches)
    {
        totalMatches = 0;
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0 || Count == 0) return new List<int>();

        var patterns = new byte[terms.Length][];
        for (int t = 0; t < terms.Length; t++)
        {
            patterns[t] = PackedStringTable.Fold(terms[t]);
            if (patterns[t].Length == 0) return new List<int>();
        }

        // A folder whose path contains a term makes every file underneath it match that term, so
        // each term needs to know which folders it hits before the per-file work starts. These
        // scans run over the ~27 MB folder blob and cost about a millisecond each.
        var folderHits = new ulong[terms.Length][];
        for (int t = 0; t < terms.Length; t++)
            folderHits[t] = ScanFolders(patterns[t], cancellationToken);

        int partitions = Count < 50_000 ? 1 : Math.Clamp(Environment.ProcessorCount, 1, 16);
        bool needFolderPass = !IsEmptyBitmap(folderHits[0]);

        // Marks entries the name pass already accounted for, so the folder pass can skip them
        // with a bit test instead of re-searching each name. Only allocated when the folder pass
        // will actually run (about 240 KB for a 2 million entry index).
        var nameMatched = needFolderPass ? new ulong[(Count >> 6) + 1] : null;

        var heaps = new RankedHitHeap[partitions];
        var counts = new int[partitions];
        var merged = new RankedHitHeap(limit);

        // Pass 1 - name matches, split across cores over the name blob. Partition boundaries are
        // aligned to 64 entries so no two threads ever write the same word of nameMatched, which
        // keeps the bitmap lock-free.
        Parallel.For(0, partitions, p =>
        {
            int lo = AlignPartition((int)((long)Count * p / partitions));
            int hi = p == partitions - 1 ? Count : AlignPartition((int)((long)Count * (p + 1) / partitions));
            var heap = new RankedHitHeap(limit);
            counts[p] = ScanNames(lo, hi, patterns, folderHits, heap, nameMatched, cancellationToken);
            heaps[p] = heap;
        });

        for (int p = 0; p < partitions; p++)
        {
            merged.AddRange(heaps[p]);
            totalMatches += counts[p];
        }

        // Pass 2 - entries that match only because their FOLDER PATH contains the first term.
        // Run once over the matching folders (never per partition), and skipped entirely when
        // no folder matched, which is the common case for a distinctive term.
        if (needFolderPass)
            totalMatches += ScanFolderMatches(patterns, folderHits, merged, nameMatched!, cancellationToken);

        // Superseded part-way through: the counts and the heap only cover the part of the index
        // that was scanned, so report nothing rather than a misleading partial answer.
        if (cancellationToken.IsCancellationRequested)
        {
            totalMatches = 0;
            return new List<int>();
        }

        return SortKeys(merged);
    }

    /// <summary>Order the retained keys and drop the packing, leaving display-ready indices.</summary>
    private List<int> SortKeys(RankedHitHeap heap)
    {
        var keys = heap.Keys.ToArray();

        // The packed key reproduces every ORDER BY column except the final "Name ASC" tie-break,
        // which is settled here - only over the handful of rows actually being displayed.
        Array.Sort(keys, (a, b) =>
        {
            int byKey = a.CompareTo(b);
            if (byKey != 0) return byKey;
            return string.CompareOrdinal(
                _names.Original(RankedHitHeap.IndexOfKey(a)),
                _names.Original(RankedHitHeap.IndexOfKey(b)));
        });

        var result = new List<int>(keys.Length);
        foreach (var key in keys)
            result.Add(RankedHitHeap.IndexOfKey(key));
        return result;
    }

    /// <summary>
    /// Walk one slice of the name blob for the first term, then confirm the remaining terms on the
    /// few entries that survived. Returns how many entries in this slice matched.
    /// </summary>
    private int ScanNames(
        int lo, int hi,
        byte[][] patterns,
        ulong[][] folderHits,
        RankedHitHeap heap,
        ulong[]? nameMatched,
        CancellationToken cancellationToken)
    {
        if (lo >= hi) return 0;

        int matches = 0;
        int probes = 0;
        var blob = _names.Blob;
        var pattern = patterns[0];
        int regionEnd = _names.StartOf(hi);
        int pos = _names.StartOf(lo);
        int hint = lo;

        // One vectorised IndexOf over the slice, jumping straight past each entry that hits so a
        // single name can never be reported twice.
        while (pos < regionEnd)
        {
            int found = blob.Slice(pos, regionEnd - pos).IndexOf(pattern);
            if (found < 0) break;

            int index = _names.IndexOfOffsetFrom(pos + found, hint, hi);

            // Stop rather than throw: a superseded search is ordinary control flow here, and the
            // partial result is discarded by the caller either way. See Search().
            if ((++probes & 0xFFFF) == 0 && cancellationToken.IsCancellationRequested) break;

            if (nameMatched != null)
                nameMatched[index >> 6] |= 1UL << (index & 63);

            if (MatchesRemainingTerms(index, patterns, folderHits))
            {
                matches++;
                heap.Add(RankedHitHeap.PackKey(IsFolder(index), RelevanceOf(index, pattern), _names.LengthOf(index), index));
            }

            hint = index + 1;
            pos = _names.StartOf(hint);
        }

        return matches;
    }

    /// <summary>Round a partition boundary down to a multiple of 64 entries.</summary>
    private static int AlignPartition(int index) => index & ~63;

    /// <summary>
    /// Entries that match only because their folder path contains the first term. The CSR map
    /// turns this into a walk of the matching folders' children instead of another sweep over
    /// every entry in the index.
    /// </summary>
    private int ScanFolderMatches(
        byte[][] patterns,
        ulong[][] folderHits,
        RankedHitHeap heap,
        ulong[] nameMatched,
        CancellationToken cancellationToken)
    {
        int matches = 0;
        var bitmap = folderHits[0];

        for (int f = 0; f < _folders.Count; f++)
        {
            // Checked before the skip below, not after it: tucked in behind the `continue` this
            // only ran for folders that both matched and sat on a 4096 boundary, so a long scan
            // could go a very long time without noticing it had been superseded.
            if ((f & 0xFFF) == 0 && cancellationToken.IsCancellationRequested) break;
            if ((bitmap[f >> 6] & (1UL << (f & 63))) == 0) continue;

            int to = _folderFileStart[f + 1];
            for (int k = _folderFileStart[f]; k < to; k++)
            {
                int index = _folderFileList[k];

                // Already counted by the name pass - one bit test, no second name search.
                if ((nameMatched[index >> 6] & (1UL << (index & 63))) != 0) continue;
                if (!MatchesRemainingTerms(index, patterns, folderHits)) continue;

                matches++;
                heap.Add(RankedHitHeap.PackKey(IsFolder(index), 4, _names.LengthOf(index), index));
            }
        }

        return matches;
    }

    /// <summary>
    /// Terms after the first are checked one entry at a time: a term matches if it is in the name
    /// or anywhere in the folder path (which the prepared bitmap answers in one bit test).
    /// </summary>
    private bool MatchesRemainingTerms(int index, byte[][] patterns, ulong[][] folderHits)
    {
        if (patterns.Length == 1) return true;

        int folder = _folderOf[index];
        var name = _names.Folded(index);

        for (int t = 1; t < patterns.Length; t++)
        {
            if ((folderHits[t][folder >> 6] & (1UL << (folder & 63))) != 0) continue;
            if (name.IndexOf(patterns[t]) >= 0) continue;
            return false;
        }

        return true;
    }

    /// <summary>1 = exact name, 2 = name starts with the term, 3 = name contains it.</summary>
    private int RelevanceOf(int index, byte[] pattern)
    {
        var name = _names.Folded(index);
        if (name.Length == pattern.Length) return 1;
        if (name.StartsWith(pattern)) return 2;
        return 3;
    }

    /// <summary>Bitmap of folder-table entries whose path contains the term.</summary>
    private ulong[] ScanFolders(byte[] pattern, CancellationToken cancellationToken)
    {
        var bitmap = new ulong[(_folders.Count >> 6) + 1];
        var blob = _folders.Blob;
        int pos = 0;

        while (pos < blob.Length)
        {
            int found = blob.Slice(pos).IndexOf(pattern);
            if (found < 0) break;

            int folder = _folders.IndexOfOffset(pos + found, 0, _folders.Count);
            bitmap[folder >> 6] |= 1UL << (folder & 63);

            if ((folder & 0x3FFF) == 0 && cancellationToken.IsCancellationRequested) break;
            pos = _folders.StartOf(folder + 1);
        }

        return bitmap;
    }

    private static bool IsEmptyBitmap(ulong[] bitmap)
    {
        foreach (var word in bitmap)
        {
            if (word != 0) return false;
        }
        return true;
    }

    #endregion
}
