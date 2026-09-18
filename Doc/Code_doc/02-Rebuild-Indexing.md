# 02 — Rebuild Indexing

Discarding the existing index and building it again from scratch, and the cheaper alternative —
**resume** — that the app offers first.

> **Related documents**
> [01-Indexing.md](01-Indexing.md) ·
> [03-File-Search.md](03-File-Search.md) ·
> [04-Database-And-Index-Writing.md](04-Database-And-Index-Writing.md) ·
> [05-Auto-Watch.md](05-Auto-Watch.md)

---

## 1. Purpose

There is exactly **one** indexing pipeline. Rebuild is not a separate code path — it is the same
`RunPipelineAsync` from [01-Indexing.md](01-Indexing.md) with `fullRebuild: true`, which adds
three things before the loop starts:

1. lock search (`_hasSearchableData = false`),
2. forget all checkpoints (`IndexingState.Reset()`),
3. drop and recreate the tables (`FileDatabase.ClearAsync()`).

Everything after that — phases, scopes, units, chunks, checkpoints, segment publishing — is
identical.

The older design had the Index button run its own full-drive scan; that is what froze the window
and pinned the disk. The button now only chooses between **resume** and **rebuild**.

---

## 2. Three user-visible entry states

`MainForm.BtnIndex_Click` ([MainForm.Indexing.cs:18](../../AnythingSearch/Forms/MainForm.Indexing.cs)) branches on the current state:

```
BtnIndex_Click
  |
  +-- _searchManager.IsIndexing ?
  |       yes -> OfferToPauseIndexing()
  |              "Would you like to pause it? Everything indexed so far is kept..."
  |              Yes -> _searchManager.CancelIndexing()
  |                     lblSearchInfo = "Indexing paused - progress saved"
  |              RETURN
  |
  +-- state.IsComplete == false  AND  state.HasSearchableData == true ?   (partial index)
  |       -> MessageBox "Resume Indexing?"  [Yes / No / Cancel]
  |          shows: items so far, count of scopes still pending
  |          Cancel -> RETURN
  |          Yes    -> StopFileWatcher(); SetIndexingUIState(true)
  |                    await _searchManager.ResumeIndexingAsync();  RETURN
  |          No     -> fall through to the rebuild below
  |
  +-- _searchManager.IsDatabaseReady ?   (complete index)
  |       -> MessageBox "Rebuild Search Index?"  [Yes / No]
  |          No -> RETURN
  |
  +-- else (no index at all)
  |       -> MessageBox "Build Search Index?"  [Yes / No]
  |          No -> RETURN
  |
  +-- StopFileWatcher()           <- it writes to the same database the build writes to
  +-- SetIndexingUIState(true)    <- disable Index/Settings, show progress bar, ApplySearchLock()
  +-- await _searchManager.RebuildIndexAsync()
```

Note the "Resume?" dialog is a **three-button** prompt: *No* falls through into the full rebuild,
*Cancel* aborts entirely.

---

## 3. Resume path

```
SearchManager.ResumeIndexingAsync(token)          [SearchManager.cs:208]
    -> BackgroundIndexingService.ResumeIndexingAsync(token)
           -> StartPipeline(fullRebuild: false, token)
           -> returns Task.CompletedTask immediately
```

Nothing is cleared. `RunPipelineAsync` runs `SyncPlanWithState()`, skips every scope already
`Completed`, and each remaining scope skips the units listed in its `CompletedUnits`.

This is the same thing startup does — `BackgroundIndexingService.InitializeAsync` ends with
`StartPipeline(fullRebuild: false)` whenever the state is not complete. Startup indexing and the
Index button therefore behave identically by construction.

Search is **not** locked on this path: `_hasSearchableData` is already true for a partial index.

---

## 4. Rebuild path

```
SearchManager.RebuildIndexAsync(token)            [SearchManager.cs:194]
  |
  +-- _useSqlite = false
  +-- _memorySearch.Invalidate()          <- drop snapshot + pending delta, reset counters
  +-- SearchSourceChanged?.Invoke(SearchSource.None)
  +-- StatusChanged?.Invoke("Rebuilding the index...")
  |
  v
BackgroundIndexingService.RebuildIndexAsync(token)   [BackgroundIndexingService.cs:182]
  |
  +-- _hasSearchableData = false      <-- FIRST statement, deliberately
  |       the database is about to be dropped, so search must be locked from this moment
  |       rather than whenever the pipeline thread gets around to starting
  |
  +-- _isIndexing ?
  |       yes -> CancelIndexing(); await WaitForIndexingAsync()
  |
  +-- StartPipeline(fullRebuild: true, token)
           |
           v
     Task.Run(RunPipelineAsync(fullRebuild: true, token))
           |
           +-- _hasSearchableData = false
           +-- lock (_stateLock) _state.Reset()      <- Scopes.Clear(), StartedAt = now,
           |                                            CompletedAt = null, Save()
           +-- await _database.ClearAsync()          <- DROP + CREATE Files and Folders
           |
           +-- ... identical to a first-run build from here on ...
```

### What `ClearAsync` does — [FileDatabase.cs:215](../../AnythingSearch/Database/FileDatabase.cs)

```
using var dbLock = await LockAsync()          <- the shared connection gate
while (_pendingInserts.TryTake(out _)) { }    <- drain the pending-insert bag
_folderCache.Clear()                          <- path -> folder id cache
DROP TABLE IF EXISTS Files
DROP TABLE IF EXISTS Folders
CREATE TABLE Folders (...)                    <- recreated empty, no indexes
CREATE TABLE Files   (...)
```

DROP + CREATE rather than `DELETE FROM` — it is the fastest clear and it also drops every index
on those tables. `PrepareForBulkIndexingAsync`, which runs immediately afterwards in
`RunPipelineAsync`, recreates the two indexes the build needs.

Clearing `_folderCache` is essential: it maps path → folder id, and every one of those ids has
just ceased to exist.

---

## 5. Full rebuild sequence

```
 UI                    SearchManager        BackgroundIndexingService      FileDatabase
  |                         |                         |                        |
  |-- btnIndex click ------>|                         |                        |
  |   (confirm dialog)      |                         |                        |
  |-- StopFileWatcher() ----|------------------------ | ---------------------> | (no more
  |-- SetIndexingUIState(true)                        |                        |  incremental
  |     btnIndex/btnSettings disabled                 |                        |  writes)
  |     progressBar.Visible = true                    |                        |
  |     ApplySearchLock() -> txtSearch.Enabled = false|                        |
  |                         |                         |                        |
  |-- RebuildIndexAsync() ->|                         |                        |
  |                         |-- _useSqlite = false    |                        |
  |                         |-- memory.Invalidate()   |                        |
  |                         |-- RebuildIndexAsync() ->|                        |
  |                         |                         |-- _hasSearchableData=false
  |                         |                         |-- cancel+await if running
  |                         |                         |-- StartPipeline(true) -+
  |<--- returns (UI free) --|                         |                        |
  |                         |                         |  [background thread]   |
  |                         |                         |-- state.Reset()        |
  |                         |                         |-- ClearAsync() ------->| DROP/CREATE
  |                         |                         |-- MarkIndexingStarted()|
  |                         |                         |-- PrepareForBulk ----->| bulk PRAGMAs
  |                         |                         |                        | + UNIQUE index
  |                         |                         |                        | + idx_folders_path
  |                         |                         |                        |
  |                         |                         |== PHASE 1: Downloads ==|
  |                         |                         |   chunk -> commit -> checkpoint
  |<-- ProgressChanged -----|<------------------------|                        |
  |                         |                         |                        |
  |<== ScopePublished("Downloads") ===================|                        |
  |    ApplySearchLock() -> txtSearch re-enabled      |                        |
  |                         |-- memory.RequestRebuild("Downloads indexed")     |
  |                         |                         |                        |
  |                         |                         |== PHASE 2: each drive =|
  |<== ScopePublished("Drive D:\ (part 1)") ==========|   (segment, if > 500k) |
  |<== ScopePublished("Drive D:\ (part 2, final)") ===|                        |
  |                         |                         |                        |
  |                         |                         |== PHASE 3: OS drive ===|
  |<== ScopePublished("C:\Users") ====================|                        |
  |<== ScopePublished("C:\Program Files (x86)") ======|                        |
  |<== ... ===========================================|                        |
  |                         |                         |                        |
  |                         |                         |-- FinalizeIndexing --->| checkpoint,
  |                         |                         |                        | runtime PRAGMAs,
  |                         |                         |                        | ANALYZE, VACUUM,
  |                         |                         |                        | checkpoint
  |                         |                         |-- MarkCompleted        |
  |<-- IndexingCompleted ---|<------------------------|                        |
  |    SetIndexingUIState(false)                      |                        |
  |    UpdateTotalCountAsync()                        |                        |
  |    if chkAutoWatch.Checked -> StartFileWatcher()  |                        |
```

---

## 6. Search during a rebuild

| Moment | `IsSearchLocked` | Search box | Source |
| --- | --- | --- | --- |
| Before `RebuildIndexAsync` | false | enabled | Memory |
| `RebuildIndexAsync` entered | **true** | **disabled** | None |
| Phase 1 (Downloads) running | true | disabled | None |
| Phase 1 published | false | enabled | SQLite, then Memory once the snapshot lands |
| Phases 2–3 running | false | enabled | Memory (refreshed on each `ScopePublished`) |
| Pipeline complete | false | enabled | Memory |

`IsSearchLocked` is simply `!_indexingService.HasSearchableData` ([SearchManager.cs:71](../../AnythingSearch/Services/Search/SearchManager.cs)).

`MainForm.ApplySearchLock` ([MainForm.Indexing.cs:136](../../AnythingSearch/Forms/MainForm.Indexing.cs)) applies it:

```
locked:
    txtSearch.Enabled = false
    dgvResults.Visible = false;  pnlRecentSearches.Visible = false
    lblSearchInfo  = "App is indexing... search will be available shortly"
    lblWatchStatus = "App is indexing..."            (Warning colour)
unlocked:
    txtSearch.Enabled = true
    empty query -> show recent searches
    still indexing -> lblWatchStatus = "Indexing continues in the background - search is available"
```

`TxtSearch_TextChanged` also short-circuits while locked, so a keystroke that slips through never
runs a query that could only return zero.

---

## 7. Pause, and what "progress is saved" means

Pausing is `CancelIndexing()` — cancel the token, complete the channel. It does **not** wait.

What survives:

* every chunk that **committed** — its rows are in SQLite and its unit keys are in
  `CompletedUnits`;
* the chunk that was in flight commits too (`RunChunkAsync`'s `finally` always calls
  `CommitBatchAsync`, "the entries already handed to the writer belong in the database, and
  leaving an open transaction behind would block every later write") — but it is **not**
  checkpointed, because `if (!token.IsCancellationRequested)` guards the `CompletedUnits` update.

So a cancelled chunk is re-walked next run. `INSERT OR IGNORE` against `UNIQUE(FolderId, Name)`
makes the repeat free (see [04-Database-And-Index-Writing.md](04-Database-And-Index-Writing.md)).

The scope is left `InProgress` in the state file; `IndexingState.Load` converts that back to
`Pending` on the next launch.

Status recorded: `MarkFailed("Indexing was cancelled - progress has been saved")` with the
progress line `"Indexing paused - progress saved, it will resume later"`.

---

## 8. Recovery paths that *force* a fresh start

Not every rebuild is user-initiated. Two guards in `InitializeAsync`
([BackgroundIndexingService.cs:121](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.cs)) reset state automatically:

```
count = await _database.GetCountAsync()

if (count == 0 && _state.Scopes.Count > 0)
    _state.Reset();
```

An empty database with recorded progress means the database file was deleted or corrupted, so
the progress is meaningless. The state is cleared and the pipeline starts over — through the
normal `fullRebuild: false` path, which now has nothing to skip.

`SyncPlanWithState()` handles the opposite case: `RemoveScopesMissingFrom` drops scopes for
drives that are no longer attached, so a removed disk cannot keep the index permanently
"incomplete".

---

## 9. Legacy data cleanup

Each release indexes into a **versioned** data folder, so the previous release's folder is dead
weight once a complete index exists in the current one. `LegacyDataCleanup.Run` is fired
(`Task.Run`, not awaited) from two places:

* `InitializeAsync`, when a complete index is found — retried on every such launch, because the
  first attempt can fail while an older copy of the app still holds its database open;
* the end of `RunPipelineAsync`, off-thread, because a recursive delete of a several-hundred-MB
  database should not hold up the end of the pipeline.

---

## 10. Performance considerations

| Concern | Detail |
| --- | --- |
| A rebuild is the *expensive* option | Resume skips completed scopes wholesale and completed units within a scope. The UI offers it first for exactly this reason. |
| Watcher must be stopped first | `StopFileWatcher()` precedes both paths. The watcher writes single entries through the same shared connection the bulk writer uses; leaving it running means both contend on `FileDatabase._gate`. |
| Snapshot invalidation is immediate | `_memorySearch.Invalidate()` drops the snapshot **and** the pending delta before the tables are dropped, so no search can be answered from a snapshot describing rows that no longer exist. |
| `VACUUM` at the end | `FinalizeIndexingAsync` runs `ANALYZE` + `VACUUM` + `wal_checkpoint(TRUNCATE)` twice. In WAL mode a `VACUUM` rebuilds the database *into* the log, so the trailing checkpoint is what actually shrinks the main file — without it a 1.3 M-entry build left a 191 MiB database beside a 192 MiB log. |
| Startup maintenance is skipped during a build | `SearchManager.RunStartupMaintenanceAsync` returns early `if (_indexingService.IsIndexing)` — a duplicate sweep would fight the writer and a compaction would be undone by the next chunk. |
| Repeated rebuilds | Nothing rate-limits the Index button beyond `StartPipeline`'s `if (_isIndexing) { ReportProgress("Indexing already in progress..."); return; }` guard, and the button is disabled by `SetIndexingUIState(true)` for the duration. |

---

## 11. Quick reference

| Operation | API | Clears DB | Clears state | Locks search |
| --- | --- | --- | --- | --- |
| Startup (any state) | `BackgroundIndexingService.InitializeAsync()` | no | only if db empty + state non-empty | no (unless nothing published yet) |
| Resume button | `SearchManager.ResumeIndexingAsync()` | no | no | no |
| Rebuild / Build button | `SearchManager.RebuildIndexAsync()` | **yes** | **yes** | **yes** |
| Pause button | `SearchManager.CancelIndexing()` | no | no (keeps checkpoints) | no |
