namespace AnythingSearch.Services.Search.Memory;

/// <summary>
/// A bounded max-heap that keeps only the best <c>capacity</c> hits seen so far.
///
/// The old SQLite query sorted every single match before applying LIMIT - for a common substring
/// that meant building a temp B-tree over hundreds of thousands of rows just to show 1,000 of
/// them. Here each hit costs one comparison against the current worst entry, and only the rare
/// hit that beats it pays the O(log capacity) sift. Scanning stays effectively linear no matter
/// how many rows match.
///
/// Hits are stored as a single packed key so the heap never dereferences the index arrays:
///
///   bit 63      folders first (0 = folder, 1 = file)      - matches "ORDER BY IsFolder DESC"
///   bits 62-59  relevance tier, 1 = best                  - matches the CASE ... END ordering
///   bits 58-43  folded name length, capped at 0xFFFF      - matches "ORDER BY LENGTH(Name) ASC"
///   bits 42-0   entry index                               - the payload
///
/// Because the fields are laid out most-significant-first, plain unsigned ordering of the packed
/// key reproduces the whole ORDER BY, and a smaller key is a better result.
/// </summary>
public sealed class RankedHitHeap
{
    private readonly ulong[] _keys;
    private int _size;

    public RankedHitHeap(int capacity)
    {
        _keys = new ulong[Math.Max(1, capacity)];
    }

    public int Count => _size;

    /// <summary>The worst key currently retained - callers may use it to skip hopeless hits.</summary>
    public ulong WorstKey => _size == 0 ? ulong.MaxValue : _keys[0];

    public bool IsFull => _size == _keys.Length;

    public const int IndexBits = 43;
    private const ulong IndexMask = (1UL << IndexBits) - 1;

    public static ulong PackKey(bool isFolder, int relevance, int nameLength, int index)
        => ((isFolder ? 0UL : 1UL) << 63)
         | ((ulong)(uint)relevance << 59)
         | ((ulong)(uint)Math.Min(nameLength, 0xFFFF) << 43)
         | ((ulong)(uint)index & IndexMask);

    public static int IndexOfKey(ulong key) => (int)(key & IndexMask);

    public void Add(ulong key)
    {
        if (_size < _keys.Length)
        {
            _keys[_size++] = key;
            SiftUp(_size - 1);
            return;
        }

        // Full: only a key better (smaller) than the current worst is worth keeping.
        if (key >= _keys[0]) return;

        _keys[0] = key;
        SiftDown(0);
    }

    public void AddRange(RankedHitHeap other)
    {
        for (int i = 0; i < other._size; i++)
            Add(other._keys[i]);
    }

    /// <summary>The retained keys, unordered. Callers sort the (small) result themselves.</summary>
    public ReadOnlySpan<ulong> Keys => _keys.AsSpan(0, _size);

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (_keys[parent] >= _keys[i]) break;
            (_keys[parent], _keys[i]) = (_keys[i], _keys[parent]);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        while (true)
        {
            int left = i * 2 + 1;
            if (left >= _size) break;

            int largest = left;
            int right = left + 1;
            if (right < _size && _keys[right] > _keys[left]) largest = right;

            if (_keys[i] >= _keys[largest]) break;
            (_keys[i], _keys[largest]) = (_keys[largest], _keys[i]);
            i = largest;
        }
    }
}
