# 03 — File Search

How a keystroke becomes a result list: source selection, the in-memory index, the SQLite
fallback, and the live-changes overlay.

> **Related documents**
> [01-Indexing.md](01-Indexing.md) ·
> [02-Rebuild-Indexing.md](02-Rebuild-Indexing.md) ·
> [04-Database-And-Index-Writing.md](04-Database-And-Index-Writing.md) ·
> [05-Auto-Watch.md](05-Auto-Watch.md)

---

## 1. Purpose

Answer substring queries over millions of paths fast enough that the result list keeps up with
typing.

`WHERE Name LIKE '%term%'` can never use a B-tree index, so the original SQLite query read every
row and joined it to the folder table on **every keystroke**. `EXPLAIN QUERY PLAN` reported
`SCAN fo / SEARCH f USING INDEX / USE TEMP B-TREE FOR ORDER BY` — three full-dataset operations
per character typed, off a 430 MB file.

The current design keeps the names in RAM and scans them, the way Everything does. SQLite
remains the durable store and the fallback.

---

## 2. Source hierarchy

```
SearchSource.Memory  -- the in-memory snapshot. Used whenever one is loaded.
SearchSource.SQLite  -- used while the snapshot is loading, and if it ever fails.
SearchSource.None    -- nothing published yet; the search box is disabled.
```

`SearchManager.CurrentSource` ([SearchManager.cs:52](../../AnythingSearch/Services/Search/SearchManager.cs)):

```csharp
_memorySearch.IsReady ? Memory : _useSqlite ? SQLite : None
```

There is **no third source**. Windows Search used to sit behind these; it was removed because it
meant two indexes were consulted, results changed shape mid-build, and the app depended on a
service the user may have disabled. Phased indexing makes the local index searchable within
seconds instead.

---

## 3. Key components

| File | Responsibility |
| --- | --- |
| [SearchManager.cs](../../AnythingSearch/Services/Search/SearchManager.cs) | Init, source selection, rebuild/status API, startup maintenance. |
| [SearchManager.Query.cs](../../AnythingSearch/Services/Search/SearchManager.Query.cs) | Query execution, fallback and circuit breaker. |
| [SearchManager.Events.cs](../../AnythingSearch/Services/Search/SearchManager.Events.cs) | Indexing-event wiring; re-exposes indexing events to `MainForm`. |
| [MemorySearchService.cs](../../AnythingSearch/Services/Search/Memory/MemorySearchService.cs) | Snapshot lifecycle, live-change delta, rebuild policy. |
| [MemorySearchService.Query.cs](../../AnythingSearch/Services/Search/Memory/MemorySearchService.Query.cs) | Runs a query and folds the delta into the result. |
| [MemoryFileIndex.cs](../../AnythingSearch/Services/Search/Memory/MemoryFileIndex.cs) | The immutable snapshot and the scan itself. |
| [MemoryIndexBuilder.cs](../../AnythingSearch/Services/Search/Memory/MemoryIndexBuilder.cs) | Loads a snapshot from SQLite over its own read-only connection. |
| [PackedStringTable.cs](../../AnythingSearch/Services/Search/Memory/PackedStringTable.cs) | Contiguous case-folded UTF-8 blob + offset table. |
| [RankedHitHeap.cs](../../AnythingSearch/Services/Search/Memory/RankedHitHeap.cs) | Bounded max-heap of packed ranking keys. |
| [FileDatabase.Search.cs](../../AnythingSearch/Database/FileDatabase.Search.cs) | The SQLite fallback queries. |
| [MainForm.Search.cs](../../AnythingSearch/Forms/MainForm.Search.cs) | Debounce, cancellation, grid population. |

---

## 4. From keystroke to grid

```
 txtSearch.TextChanged                               [MainForm.Search.cs:36]
   |
   +-- _searchManager.IsSearchLocked ? -> cancel pending, show status, RETURN
   +-- placeholder / whitespace ?      -> cancel, hide grid, show recent searches, RETURN
   +-- length < 2 ?                    -> cancel, "Type at least 2 characters...", RETURN
   |
   +-- token = CancelPendingSearch()   <- cancels + disposes the previous CTS, makes a new one
   +-- lblSearchInfo = "Searching..."
   |
   +-- await Task.Delay(debounce, token)
   |        debounce = CurrentSource == Memory ?  90 ms  :  350 ms
   |        (90 ms is below the threshold where typing feels laggy; SQLite is slow enough
   |         that the longer window is still worth it)
   |
   +-- await PerformSearchAsync(searchText, token)
            |
            +-- (results, totalMatches, source) = await _searchManager.SearchAsync(text, 1000, token)
            +-- cancelled? -> RETURN
            +-- displayResults = results.Take(1000)
            +-- pnlRecentSearches.Visible = false
            +-- PopulateResultGrid(displayResults)     <- one Rows.AddRange, not per-row Add
            +-- dgvResults.Visible = true
            +-- RefreshVisibleIcons()                  <- AFTER rows are on screen
            +-- SaveToRecentSearches(...)
            +-- lblSearchInfo = "Found N results (showing M) • Xms • Instant|Local DB"
   |
   +-- catch (OperationCanceledException) -> superseded by a newer keystroke, nothing to do
```

`PopulateResultGrid` clones `dgvResults.RowTemplate`, builds the whole `DataGridViewRow[]`, then
does a single `Rows.Clear()` + `Rows.AddRange(rows)` inside `SuspendLayout`/`ResumeLayout`.
Adding rows one at a time re-runs the Fill column layout per row.

Each row keeps its `FileEntry` in `row.Tag` so the icon backfill does not have to re-parse
display text.

---

## 5. Source selection and fallback

`SearchManager.SearchAsync` ([SearchManager.Query.cs:39](../../AnythingSearch/Services/Search/SearchManager.Query.cs)):

```
SearchAsync(query, maxResults = 1000, token)
  |
  +-- query blank ?        -> ([], 0, CurrentSource)
  +-- IsSearchLocked ?     -> ([], 0, SearchSource.None)
  |
  +-- _memorySearch.IsReady ?
  |     try   -> RunMemorySearchAsync -> (results, total, Memory)   [RETURN]
  |     catch OperationCanceledException -> rethrow (superseded keystroke)
  |     catch Exception ->
  |            StatusChanged("In-memory search failed: ... - using the database")
  |            _memorySearch.Invalidate()
  |            fall through
  |
  +-- try   -> RunSqliteSearchAsync
  |            _consecutiveSqliteFailures = 0
  |            return (results, results.Count, SQLite)
  |
  +-- catch OperationCanceledException -> rethrow (not a SQLite failure)
  +-- catch Exception ->
         _consecutiveSqliteFailures++
         StatusChanged("Search failed: ...")
         if _consecutiveSqliteFailures >= 3:            <- circuit breaker
             _useSqlite = false
             SearchSourceChanged(CurrentSource)
             StatusChanged("The local database keeps failing - rebuild the index to restore search.")
         return ([], 0, SearchSource.None)
```

The return value is a tuple `(Results, TotalMatches, Source)` rather than state on the manager:
searches overlap, and a superseded query finishing late would otherwise overwrite the count
belonging to the one actually on screen.

### Threading

> **Microsoft.Data.Sqlite does not implement real asynchronous I/O** — its `*Async` methods run
> synchronously on the calling thread.

Consequences, both handled:

* `RunSqliteSearchAsync` takes `_searchGate` (a `SemaphoreSlim(1,1)`, because the connection is
  shared) and then runs the query inside `Task.Run(..., token)`.
* `RunMemorySearchAsync` uses `Task.Run` **without** passing the token — that overload throws
  `TaskCanceledException` when the token is already set at scheduling time, which on a search box
  is simply what happens when someone types quickly. The scan checks the token internally and
  returns early instead, so a superseded search on this path costs no exception at all.
* `SearchManager.GetTotalCountAsync` wraps `_database.GetCountAsync()` in `Task.Run` for the same
  reason — `COUNT(*)` over a large index must never run on the UI thread.

---

## 6. The in-memory snapshot

### 6.1 Structure — `MemoryFileIndex`

```
MemoryFileIndex  (immutable; published with a single reference assignment)
  _names          PackedStringTable   one entry per file/folder, case-folded UTF-8
  _folders        PackedStringTable   one entry per distinct folder path
  _folderOf       int[Count]          file index  -> folder table index
  _sizes          long[Count]
  _modified       int[Count]          seconds since 2000-01-01 (packed)
  _isFolderBits   ulong[]             one bit per entry
  _folderFileStart int[FolderCount+1] |  CSR layout: folder -> its files,
  _folderFileList  int[Count]         |  built once in the constructor
```

The CSR (compressed sparse row) folder map lets a folder-path match expand straight to the files
underneath it instead of re-scanning every entry.

### 6.2 `PackedStringTable`

```
_blob       [n][a][m][e][0][n][e][x][t][0]...    folded UTF-8, NUL after every entry
_start      int[Count + 1]                        _start[i] = first byte of entry i
_upperBits  ulong[]                               bit per blob byte: set => restore by -32
_overrides  Dictionary<int,string>                non-ASCII entries, kept verbatim
```

Why this layout:

* A case-insensitive "contains" over the whole table is **one** `ReadOnlySpan<byte>.IndexOf` pass,
  which the runtime vectorises (SSE2/AVX2). Scanning ~70 MB costs a few milliseconds instead of
  millions of individual string comparisons.
* The NUL separators mean a match can never straddle two entries — a search term can never
  contain NUL.
* The original casing is **not** stored twice. For 3.3 M file names the `_upperBits` approach
  costs ~9 MB instead of the ~70 MB a second blob would need.
* A hit's byte offset is mapped back to an entry index only for positions that actually matched:
  `IndexOfOffsetFrom` probes up to 8 entries forward from the scan hint before falling back to a
  bisect. For a very common term the hint *is* the answer, which avoids ~21 binary-search steps
  per match.

### 6.3 `RankedHitHeap` — packed ranking key

```
 bit 63     folders first (0 = folder, 1 = file)     == ORDER BY IsFolder DESC
 bits 62-59 relevance tier, 1 = best                 == the CASE ... END ordering
 bits 58-43 folded name length, capped 0xFFFF        == ORDER BY LENGTH(Name) ASC
 bits 42-0  entry index                              == the payload
```

Because the fields are most-significant-first, plain unsigned ordering of the key reproduces the
whole `ORDER BY`, and **a smaller key is a better result**. Each hit costs one comparison against
the current worst entry; only a hit that beats it pays the O(log capacity) sift. Scanning stays
effectively linear no matter how many rows match — the old query sorted *every* match in a temp
B-tree before applying `LIMIT`.

---

## 7. The scan

`MemoryFileIndex.Search` ([MemoryFileIndex.cs:136](../../AnythingSearch/Services/Search/Memory/MemoryFileIndex.cs)):

```
Search(query, limit, token, out totalMatches)
  |
  +-- terms = query.Split(' ')                    AND semantics across terms
  +-- patterns[t] = PackedStringTable.Fold(terms[t])
  |
  +-- PREPARE: folderHits[t] = ScanFolders(patterns[t])
  |      one IndexOf pass over the ~27 MB folder blob per term (~1 ms each)
  |      -> bitmap of folder-table entries whose PATH contains the term
  |
  +-- partitions = Count < 50,000 ? 1 : clamp(ProcessorCount, 1, 16)
  +-- needFolderPass = folderHits[0] is not empty
  +-- nameMatched = needFolderPass ? new ulong[(Count >> 6) + 1] : null
  |
  +-- PASS 1  Parallel.For(0, partitions, p => ScanNames(lo, hi, ...))
  |      partition boundaries aligned DOWN to multiples of 64 entries (AlignPartition)
  |      so no two threads ever write the same word of nameMatched -> lock-free bitmap
  |
  |      ScanNames walks its slice of the name blob for patterns[0]:
  |          found = blob.Slice(pos, regionEnd - pos).IndexOf(pattern)   <- vectorised
  |          index = _names.IndexOfOffsetFrom(pos + found, hint, hi)
  |          mark nameMatched[index]
  |          if MatchesRemainingTerms(index, ...)  -> matches++, heap.Add(PackKey(...))
  |          hint = index + 1;  pos = _names.StartOf(hint)   <- jump past the whole entry,
  |                                                             so one name can never hit twice
  |          cancellation checked every 65,536 probes -> break (no throw)
  |
  +-- merge per-partition heaps into `merged`; sum per-partition counts
  |
  +-- PASS 2  (only if needFolderPass)  ScanFolderMatches
  |      walk only the folders whose bit is set, and only their children via the CSR map
  |      skip entries already marked in nameMatched (one bit test, no second name search)
  |      relevance for these is fixed at 4 ("path contains")
  |      cancellation checked every 4096 folders, BEFORE the skip
  |
  +-- cancelled ? -> totalMatches = 0; return []      (a partial answer would be misleading)
  |
  +-- SortKeys(merged): Array.Sort over the retained keys only, with a
      string.CompareOrdinal tie-break on name — the final "Name ASC" column,
      settled over just the rows being displayed.
```

`MatchesRemainingTerms` checks terms 2..n per surviving entry: a term matches if it is in the
name **or** anywhere in the folder path, and the folder half is one bit test against the
prepared bitmap.

**Cancellation does not throw here, by design.** Every keystroke supersedes the search before it,
so cancellation is the normal case; throwing meant building and unwinding an exception through
several frames per character typed, and it stopped the debugger on a non-fault.

---

## 8. The live-changes overlay (delta)

The snapshot is immutable, so `MemorySearchService` layers file-watcher changes on top of it.

```
_pending : ConcurrentDictionary<string, FileEntry?>   (OrdinalIgnoreCase)
             value != null -> current state of that path (added or rewritten since the snapshot)
             value == null -> deleted since the snapshot
_pendingTotal / _pendingRemoved : int, maintained with Interlocked
```

One map, not an "added dictionary + removed set", because a search needs exactly **one** question
answered per result row — "does the delta speak for this path?" — and that is one lookup.

`Mutate(path, entry)` ([MemorySearchService.cs:158](../../AnythingSearch/Services/Search/Memory/MemorySearchService.cs)) is constant time and takes no lock a search
could wait on. `ConcurrentDictionary.Count` is deliberately **not** used — it takes every
internal lock — so the counters are tracked separately and are approximate under concurrency by
design.

### Folding the delta into a result — [MemorySearchService.Query.cs:23](../../AnythingSearch/Services/Search/Memory/MemorySearchService.Query.cs)

```
TrySearch(query, limit, token, out results, out totalMatches)
  |
  +-- snapshot == null ? -> return false   (caller falls back to SQLite)
  |
  +-- indices = snapshot.Search(query, limit, token, out totalMatches)
  +-- pending = Pending;  hasPending = _pendingTotal > 0      <- read ONCE
  |
  +-- for each index:
  |       if hasPending && pending.ContainsKey(snapshot.PathOf(index))
  |            -> totalMatches--; skip      (deleted since, or rewritten since)
  |       else -> results.Add(snapshot.Materialize(index))
  |
  +-- if hasPending: MergeAdded(...)
          walk the whole delta:
              null value        -> skip (deleted)
              !MatchesAllTerms  -> skip
              else              -> results.Add(entry); added++
          cancellation checked every 1024 probes -> return
          if added > 0:
              totalMatches += added
              results.Sort(Compare(a, b, terms[0]))     <- same ordering as the snapshot
              trim to `limit`
```

`Compare`/`Relevance` reproduce the snapshot's ordering in managed code: folders first, then
relevance (1 exact, 2 starts-with, 3 contains, 4 path-only), then shortest name, then ordinal.

`MergeAdded` walks the **entire** delta, which is why its size is capped.

---

## 9. Snapshot rebuild policy

```
DeltaRebuildThreshold = 20,000    rebuild once this many pending changes accumulate
DeltaOverflowLimit    = 60,000    past this, DROP the delta and treat the snapshot as stale
QuietRebuildDelayMs   = 120,000   rebuild 2 min after the last change, so a quiet machine is exact
RebuildCooldownMs     = 15,000    minimum gap between rebuilds
```

```
Mutate(...)
  |
  +-- total > 60,000 ?
  |      Interlocked.CompareExchange(_pending, NewPendingMap(), pending)   <- REPLACE, not clear,
  |      reset counters                                                       so a search walking
  |      RequestRebuild("more changes than the overlay can carry", respectCooldown: true)
  |                                                                           the old map is fine
  +-- total >= 20,000 ? -> RequestRebuild("delta threshold reached", respectCooldown: true)
  |
  +-- total <= 2 || (total & 0xFF) == 0 ?
         _rebuildTimer.Change(120,000, Infinite)     <- throttled: rescheduling the timer for
                                                        every one of thousands of events contends
                                                        on the shared timer queue for no benefit
```

Pushing the deadline out less often only ever makes the rebuild happen *sooner*, never later.

```
RequestRebuild(reason, respectCooldown = false)
  |
  +-- respectCooldown && since-last-rebuild < 15 s ?
  |      -> _rebuildTimer.Change(remaining, Infinite); RETURN
  |      (churn-driven rebuilds wait; the ones that make the app usable at all —
  |       initial load, a phase publishing — do not)
  |
  +-- Interlocked.CompareExchange(_rebuildInFlight, 1, 0) != 0 ?
  |      -> _rebuildQueued = 1; RETURN
  |      (remembered, not dropped: phased indexing publishes one drive after another,
  |       so a request landing mid-rebuild is normal)
  |
  +-- Task.Run:
         consumedFrom = Pending;  consumed = consumedFrom.ToArray()
         index = MemoryIndexBuilder.Build(_databasePath, null, CancellationToken.None)
         if Pending is still consumedFrom:
             retire each captured entry with TryRemove(KeyValuePair)      <- reference compare,
             decrement counters                                              so a path rewritten
                                                                             mid-rebuild keeps its
                                                                             newer entry
         _snapshot = index                                <- single reference assignment
         GC.Collect(2, Forced, blocking: false)           <- BACKGROUND collection, see below
         StatusChanged("Instant search ready - N items in M MB (T ms, <reason>)")
         SnapshotReady?.Invoke()
     finally:
         _firstSnapshot.TrySetResult()
         _lastRebuildTicks = TickCount64   (stamped BEFORE the flag clears, so the cooldown covers the gap)
         _rebuildInFlight = 0
         if _rebuildQueued was 1 -> _rebuildTimer.Change(15,000, Infinite)   <- via the timer,
                                                                                never straight back
```

Changes that arrive **during** a rebuild are kept: they may or may not be in the snapshot, and a
redundant one is harmless while a lost one is not.

**The GC call must stay non-blocking.** It previously asked for `GCCollectionMode.Aggressive`
with `blocking: true`, `compacting: true` and `LargeObjectHeapCompactionMode.CompactOnce`, which
suspends every managed thread while compacting a LOH holding the whole index. Back to back,
driven by file-system churn, those froze searches for over a minute.

---

## 10. Building a snapshot — `MemoryIndexBuilder.Build`

```
connection = "Data Source=<db>;Mode=ReadOnly;Cache=Private;Pooling=False"
```

* **Its own read-only connection**, not the shared one — building never blocks the watcher, the
  catch-up pass, or a search running on the old snapshot. WAL mode lets readers and the writer
  proceed concurrently.
* **`Pooling=False` matters**: Microsoft.Data.Sqlite pools by default, so a disposed connection
  keeps the file open in the background. A lingering reader stops SQLite from taking the
  exclusive lock `VACUUM` needs, which silently defeated `CompactIfFragmentedAsync`.
* PRAGMAs: `temp_store = MEMORY`, `cache_size = -8000` (8 MB — a sequential read of every row
  gains nothing from a large page cache), `mmap_size = 128 MB`.

```
Build(databasePath, progress, token)
  |
  +-- fileCount, folderCount, maxFolderId   (three scalar queries)
  +-- folderIndexById = int[maxFolderId + 2], filled with -1
  |      folder ids are assigned by SQLite and can have gaps -> map id to a dense table index
  |
  +-- SELECT Id, Path FROM Folders          -> folderTable.Add(path)
  +-- unknownFolder = folderTable.Add("")   <- a row written between the two queries still
  |                                            gets searched by name instead of being dropped
  +-- folders = folderTable.Build()
  |
  +-- SELECT Name, FolderId, Size, Modified, IsFolder FROM Files
  |      per row: names.Add, folderOf, sizes, modified (packed), isFolderBits
  |      arrays doubled via Grow() when they fill (the counts can be stale)
  |      cancellation + progress every 65,536 rows
  |
  +-- new MemoryFileIndex(names.Build(), folders, Trim(...), ..., count)
         -> the constructor builds the CSR folder map
```

This is the **only** place that reads the whole Files table, and it does so once per snapshot
(a few seconds for 3.3 M rows) instead of once per keystroke.

---

## 11. The SQLite fallback

[FileDatabase.Search.cs](../../AnythingSearch/Database/FileDatabase.Search.cs) — used while the snapshot loads and if it fails. Written
for correctness and predictable query plans rather than per-keystroke latency.

`SearchAsync` (single-term) is **two scans joined by `UNION ALL`**, not one `OR` across a join:

```sql
SELECT m.Name, fo.Path || '\' || m.Name AS FullPath, m.Ext, m.Size, m.Modified, m.IsFolder
FROM (
    SELECT ..., CASE WHEN Name = @exact                     THEN 1
                     WHEN Name LIKE @startsWith ESCAPE '\'  THEN 2
                     ELSE 3 END AS Relevance
    FROM Files WHERE Name LIKE @contains ESCAPE '\'
  UNION ALL
    SELECT ..., 4 AS Relevance
    FROM Files
    WHERE FolderId IN (SELECT Id FROM Folders WHERE Path LIKE @contains ESCAPE '\')
      AND Name NOT LIKE @contains ESCAPE '\'
) m
INNER JOIN Folders fo ON m.FolderId = fo.Id
ORDER BY m.IsFolder DESC, m.Relevance ASC, LENGTH(m.Name) ASC, m.Name ASC
LIMIT @limit
```

The `OR` form made SQLite drive the query from the Folders table (`SCAN fo` plus an index probe
into Files for every folder), walking the whole dataset and then sorting every match in a temp
B-tree. Here Files is scanned once for name matches, folders are matched separately, and Folders
is joined only for rows that survived.

`SearchAdvancedAsync` handles multi-term queries (`query.Contains(' ')`): each space-separated
term must match `f.Name` **or** `fo.Path`, ANDed together.

Both loops call `cancellationToken.ThrowIfCancellationRequested()` **per row**, so a superseded
search stops reading immediately. Both escape LIKE wildcards via `EscapeLike` (`\`, `%`, `_`)
paired with an `ESCAPE '\'` clause.

Note the fallback's `TotalMatches` is just `results.Count` (capped at `limit`); only the
in-memory path reports the real total.

---

## 12. Startup and maintenance interaction

```
SearchManager.InitializeAsync()                       [SearchManager.cs:106]
  |
  +-- StatusChanged("Checking local index...")
  +-- await _indexingService.InitializeAsync()         <- decides complete/partial/missing
  +-- !IsDatabaseReady ? -> RETURN                     (nothing to search yet)
  |
  +-- _useSqlite = true
  +-- SearchSourceChanged(SQLite)
  +-- StatusChanged("Using local database (N items)")
  +-- _memorySearch.Start()                            -> RequestRebuild("initial load")
  +-- Task.Run(RunStartupMaintenanceAsync)             <- fire and forget
```

```
RunStartupMaintenanceAsync()                          [SearchManager.cs:142]
  +-- await _memorySearch.FirstSnapshotSettled        <- both read the same file; VACUUM needs
  |                                                      it to itself, so running them together
  |                                                      just means the compaction loses the race
  +-- _indexingService.IsIndexing ? -> RETURN          <- a later phase is still writing
  |
  +-- removed  = await db.EnsureUniqueEntriesAsync()
  +-- removed += await db.PruneOrphanFoldersAsync()
  +-- removed > 0 ? -> _memorySearch.RequestRebuild("N stale entries removed")
  +-- await db.DropUnusedIndexesAsync()
  +-- await db.CompactIfFragmentedAsync()
  |
  +-- catch -> Logger.Log("Startup database maintenance skipped: ...")   (best effort only)
```

Orphan folder rows are not harmless: the snapshot loads every folder path into one contiguous
blob and every search scans that blob once per term, so orphans make every keystroke scan
further for results that no longer exist. See
[04-Database-And-Index-Writing.md](04-Database-And-Index-Writing.md).

---

## 13. Events

| Event | Source | Effect |
| --- | --- | --- |
| `SearchSourceChanged(SearchSource)` | `SearchManager` | `MainForm.OnSearchSourceChanged` → refresh UI, start watcher, update tray. |
| `StatusChanged(string)` | `SearchManager` (relays `FileDatabase.MaintenanceStatusChanged` and `MemorySearchService.StatusChanged`) | `MainForm.OnSearchManagerStatus` — shown only while indexing. |
| `SnapshotReady` | `MemorySearchService` | relayed by `SearchManager` as `SearchSourceChanged(Memory)`. |
| `DatabaseReady` | indexer | `_useSqlite = true`, failures reset, `RequestRebuild("database ready")`. |
| `ScopePublished(label)` | indexer | `_useSqlite = true`, failures reset, `RequestRebuild("<label> indexed")`. |

---

## 14. Performance considerations

| Area | Note |
| --- | --- |
| Debounce | 90 ms on memory, 350 ms on SQLite. The in-memory index answers in single-digit ms, so the debounce only stops a fast typist queueing work. |
| Result cap | `MaxDisplayedResults = 1000` both as the query limit and as the grid cap. |
| Parallel scan | `Parallel.For` over up to 16 partitions; single partition below 50,000 entries (the coordination is not worth it). |
| Bounded heap | Avoids sorting every match — the dominant cost of the old SQL `ORDER BY` + `LIMIT`. |
| Folder pre-pass | Two folder-blob scans per term (~1 ms each over ~27 MB) buy one bit test per entry afterwards, and pass 2 is skipped entirely when no folder matched — the common case for a distinctive term. |
| Delta size | `MergeAdded` walks the whole delta on every search; `DeltaOverflowLimit = 60,000` is what bounds that cost. Without the cap, searching would get slower the busier the disk is — exactly backwards. |
| Rebuild cost | A rebuild reads every row in the database. `RebuildCooldownMs = 15,000` plus routing queued rebuilds through the timer is what stops a busy disk turning into a continuous rebuild loop. |
| Memory | Roughly one index's worth, reported in the status line as `index.ApproximateBytes`. The background GC after a rebuild keeps the process from holding two. |
| Icons | Resolved **after** rows are on screen and only for visible rows (`RefreshVisibleIcons`). Waiting for them is what made a file-heavy search take seconds while a folder-only search was instant. |
| `_searchGate` | Serializes SQLite searches on the shared connection. The in-memory path needs no gate — its snapshot is immutable. |

---

## 15. Data flow summary

```
  keystroke
     |
     v
  MainForm.TxtSearch_TextChanged -- debounce 90/350 ms, CTS per search
     |
     v
  SearchManager.SearchAsync
     |
     +--[snapshot ready]--> Task.Run -> MemorySearchService.TrySearch
     |                                     |
     |                                     +-> MemoryFileIndex.Search
     |                                     |     ScanFolders (per term)
     |                                     |     Parallel ScanNames  -> RankedHitHeap
     |                                     |     ScanFolderMatches   -> RankedHitHeap
     |                                     |     SortKeys
     |                                     +-> filter/merge _pending delta
     |                                     +-> snapshot.Materialize(index) -> FileEntry
     |
     +--[no snapshot / failed]--> _searchGate -> Task.Run -> FileDatabase.SearchAsync
     |                                                    or SearchAdvancedAsync
     v
  (List<FileEntry>, TotalMatches, SearchSource)
     |
     v
  MainForm.PopulateResultGrid -> dgvResults.Rows.AddRange -> RefreshVisibleIcons
```
