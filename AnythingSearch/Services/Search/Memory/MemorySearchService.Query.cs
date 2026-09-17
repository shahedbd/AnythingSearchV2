using AnythingSearch.Models;

namespace AnythingSearch.Services.Search.Memory;

/// <summary>
/// Query side of <see cref="MemorySearchService"/>: runs a search against the immutable snapshot
/// and folds the pending file-system changes on top of the result.
///
/// See MemorySearchService.cs for the snapshot lifecycle, the delta that is layered here, and the
/// rebuild policy that keeps that delta small.
/// </summary>
public sealed partial class MemorySearchService
{
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
    /// Fold entries created since the snapshot into the result list. This scans the whole delta,
    /// so its cost is bounded only by how large the delta is allowed to grow - which is what
    /// <see cref="DeltaOverflowLimit"/> exists to cap.
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
}
