using System.Text;

namespace AnythingSearch.Services.Search.Memory;

/// <summary>
/// A write-once, read-many table of strings packed for substring scanning.
///
/// Every string is stored ONCE, case-folded, as UTF-8 inside a single contiguous blob with a
/// NUL separator after each entry. That layout is what makes the search fast:
///
/// * A case-insensitive "contains" over the whole table becomes one linear pass of
///   ReadOnlySpan&lt;byte&gt;.IndexOf over the blob, which the runtime vectorises (SSE2/AVX2).
///   Scanning ~70 MB costs a few milliseconds instead of the 3.3 million individual string
///   comparisons a per-entry loop would do.
/// * The NUL separators mean a match can never straddle two entries, because a search term can
///   never contain NUL.
/// * A hit's byte offset is mapped back to its entry index with a binary search over
///   <see cref="_start"/>, which only happens for the handful of positions that actually matched.
///
/// The original casing is NOT stored a second time - that would double the memory. Instead every
/// byte folded from ASCII 'A'-'Z' records a bit in <see cref="_upperBits"/>, and the rare entries
/// containing non-ASCII characters keep their original string in <see cref="_overrides"/>. For
/// 3.3 million file names that costs ~9 MB instead of the ~70 MB a second blob would need.
/// </summary>
public sealed class PackedStringTable
{
    private readonly byte[] _blob;       // folded UTF-8, NUL after every entry
    private readonly int[] _start;       // _start[i] = first byte of entry i; length Count + 1
    private readonly ulong[] _upperBits; // bit per blob byte: set => restore by subtracting 32
    private readonly Dictionary<int, string> _overrides; // entries that are not pure ASCII

    public int Count { get; }

    /// <summary>Approximate bytes retained by this table, for diagnostics.</summary>
    public long ApproximateBytes =>
        _blob.LongLength + _start.LongLength * 4L + _upperBits.LongLength * 8L + _overrides.Count * 64L;

    private PackedStringTable(byte[] blob, int[] start, ulong[] upperBits, Dictionary<int, string> overrides, int count)
    {
        _blob = blob;
        _start = start;
        _upperBits = upperBits;
        _overrides = overrides;
        Count = count;
    }

    /// <summary>The whole folded blob - callers scan this directly.</summary>
    public ReadOnlySpan<byte> Blob => _blob;

    /// <summary>The folded bytes of one entry, without its NUL separator.</summary>
    public ReadOnlySpan<byte> Folded(int index) =>
        _blob.AsSpan(_start[index], _start[index + 1] - _start[index] - 1);

    /// <summary>Length in folded bytes of one entry, used for relevance tie-breaking.</summary>
    public int LengthOf(int index) => _start[index + 1] - _start[index] - 1;

    /// <summary>First blob byte belonging to entry <paramref name="index"/>.</summary>
    public int StartOf(int index) => _start[index];

    /// <summary>
    /// Map a byte offset back to its entry while walking the blob forward.
    ///
    /// <paramref name="hint"/> is the first entry the offset can possibly belong to (the scan
    /// resumes at that entry's first byte), and for a term that matches most entries it IS the
    /// answer. Probing a few entries directly before falling back to a bisect is what keeps a
    /// very common term - where nearly every entry hits - from paying 21 binary-search steps per
    /// match. On a 1.9 million entry index that alone was most of the query time.
    /// </summary>
    public int IndexOfOffsetFrom(int offset, int hint, int highEntry)
    {
        int limit = Math.Min(hint + 8, highEntry);
        for (int i = hint; i < limit; i++)
        {
            if (offset < _start[i + 1]) return i;
        }
        return IndexOfOffset(offset, limit - 1, highEntry);
    }

    /// <summary>
    /// Map a byte offset inside <see cref="Blob"/> back to the entry containing it. Only called
    /// for positions that already matched, so the O(log n) cost is irrelevant to the scan.
    /// </summary>
    public int IndexOfOffset(int offset, int lowEntry, int highEntry)
    {
        // Largest i in [lowEntry, highEntry) with _start[i] <= offset
        int lo = lowEntry, hi = highEntry - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (_start[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Rebuild the original, correctly-cased string for one entry.</summary>
    public string Original(int index)
    {
        if (_overrides.TryGetValue(index, out var original))
            return original;

        int from = _start[index];
        int len = _start[index + 1] - from - 1;
        if (len == 0) return string.Empty;

        return string.Create(len, (Table: this, From: from, Len: len), static (dest, state) =>
        {
            var blob = state.Table._blob;
            var bits = state.Table._upperBits;
            for (int i = 0; i < state.Len; i++)
            {
                int p = state.From + i;
                byte b = blob[p];
                if ((bits[p >> 6] & (1UL << (p & 63))) != 0) b -= 32;
                dest[i] = (char)b;
            }
        });
    }

    /// <summary>
    /// Case-fold a search term the same way <see cref="Builder.Add"/> folds stored entries, so the
    /// two can be compared byte-for-byte. ASCII is folded byte-wise; anything else is lowered with
    /// the invariant culture and encoded as UTF-8.
    /// </summary>
    public static byte[] Fold(string value)
    {
        bool ascii = true;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7F) { ascii = false; break; }
        }

        if (!ascii)
            return Encoding.UTF8.GetBytes(value.ToLowerInvariant());

        var bytes = new byte[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bytes[i] = (byte)(c >= 'A' && c <= 'Z' ? c + 32 : c);
        }
        return bytes;
    }

    /// <summary>
    /// Packs strings in the order they are added. The table produced by <see cref="Build"/> is
    /// immutable and safe to share across threads.
    /// </summary>
    public sealed class Builder
    {
        private byte[] _blob;
        private int[] _start;
        private ulong[] _upperBits;
        private readonly Dictionary<int, string> _overrides = new();
        private int _count;
        private int _length;

        public Builder(int expectedCount, long expectedBytes)
        {
            _blob = new byte[Math.Max(1024, Math.Min(expectedBytes, Array.MaxLength))];
            _start = new int[Math.Max(16, expectedCount + 1)];
            _upperBits = new ulong[(_blob.Length >> 6) + 2];
        }

        public int Count => _count;

        public int Add(string value)
        {
            int index = _count;
            EnsureCapacity(value.Length * 3 + 1);

            int entryStart = _length;
            _start[index] = entryStart;

            bool ascii = true;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c > 0x7F) { ascii = false; break; }

                if (c >= 'A' && c <= 'Z')
                {
                    _blob[_length] = (byte)(c + 32);
                    _upperBits[_length >> 6] |= 1UL << (_length & 63);
                }
                else
                {
                    _blob[_length] = (byte)c;
                }
                _length++;
            }

            if (!ascii)
            {
                // Non-ASCII entry: rewind, store folded UTF-8 and keep the original verbatim so
                // Original() never has to undo a fold it cannot express as a single case bit.
                ClearBits(entryStart, _length);
                _length = entryStart;

                var folded = Encoding.UTF8.GetBytes(value.ToLowerInvariant());
                EnsureCapacity(folded.Length + 1);
                folded.CopyTo(_blob, _length);
                _length += folded.Length;
                _overrides[index] = value;
            }

            _blob[_length++] = 0; // separator - keeps matches from straddling entries
            _count++;
            return index;
        }

        private void ClearBits(int from, int toExclusive)
        {
            for (int p = from; p < toExclusive; p++)
                _upperBits[p >> 6] &= ~(1UL << (p & 63));
        }

        private void EnsureCapacity(int extraBytes)
        {
            if (_count + 1 >= _start.Length)
                Array.Resize(ref _start, _start.Length * 2);

            if (_length + extraBytes <= _blob.Length) return;

            long needed = Math.Max((long)_blob.Length * 2, (long)_length + extraBytes);
            if (needed > Array.MaxLength) needed = Array.MaxLength;
            Array.Resize(ref _blob, (int)needed);
            Array.Resize(ref _upperBits, (_blob.Length >> 6) + 2);
        }

        public PackedStringTable Build()
        {
            Array.Resize(ref _start, _count + 1);
            _start[_count] = _length;

            if (_length != _blob.Length)
                Array.Resize(ref _blob, _length);
            Array.Resize(ref _upperBits, (_length >> 6) + 2);

            return new PackedStringTable(_blob, _start, _upperBits, _overrides, _count);
        }
    }
}
