# 01 — Initial Indexing

How AnythingSearch builds its file index from scratch (or resumes a partial one) the first time
it runs and on every subsequent launch.

> **Related documents**
> [02-Rebuild-Indexing.md](02-Rebuild-Indexing.md) ·
> [03-File-Search.md](03-File-Search.md) ·
> [04-Database-And-Index-Writing.md](04-Database-And-Index-Writing.md) ·
> [05-Auto-Watch.md](05-Auto-Watch.md)

---

## 1. Purpose

Build a queryable index of every file and folder on the machine's fixed drives, without:

* blocking the UI thread,
* pinning the disk at 100 % for the whole run,
* making the user wait for a full disk walk before they can search anything,
* losing all progress when the app is closed mid-build.

The design answers these with four ideas: **phases**, **scopes**, **units/checkpoints**, and a
**bounded producer/consumer pipeline**.

---

## 2. Terminology

These terms are used consistently across all five documents.

| Term | Meaning | Type in code |
| --- | --- | --- |
| **Phase** | One of the three ordered stages of a build. | `IndexPhase` ([IndexingState.cs:13](../../AnythingSearch/Models/IndexingState.cs)) |
| **Scope** | The publishing granularity — phase 1 as a whole, one data drive, or one OS-drive sub-phase. When a scope finishes, its data becomes searchable. | `IndexScopeDefinition` ([IndexPlanner.cs:24](../../AnythingSearch/Services/Indexing/IndexPlanner.cs)) |
| **Unit** (scan root) | The **checkpoint** granularity — one directory, walked recursively or files-only. | `ScanRoot` ([IndexPlanner.cs:11](../../AnythingSearch/Services/Indexing/IndexPlanner.cs)) |
| **Chunk** | A handful of units walked in parallel and committed to SQLite together. | local in `RunChunkAsync` |
| **Segment** (sub-phase) | A mid-scope publish point for a scope that keeps growing. | `IndexSegmentState` |
| **Walker** | One thread walking one unit. Count is chosen per drive. | `IndexingCapacity.WalkersFor` |
| **Snapshot** | The immutable in-memory search index built from SQLite. | `MemoryFileIndex` |

---

## 3. Key components

| File | Responsibility |
| --- | --- |
| [BackgroundIndexingService.cs](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.cs) | Lifecycle, public API, events, cancellation/shutdown. |
| [BackgroundIndexingService.Pipeline.cs](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.Pipeline.cs) | The phase → scope → chunk → checkpoint loop. |
| [BackgroundIndexingService.DirectoryScanning.cs](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.DirectoryScanning.cs) | Producer: the actual disk walk, throttling, back-pressure. |
| [BackgroundIndexingService.Consumer.cs](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.Consumer.cs) | Consumer: drains the channel into SQLite, reports progress. |
| [BackgroundIndexingService.Segments.cs](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.Segments.cs) | Mid-scope publishing for oversized scopes. |
| [BackgroundIndexingService.CatchUp.cs](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.CatchUp.cs) | Reconciliation of an existing index — documented in [05-Auto-Watch.md](05-Auto-Watch.md). |
| [IndexPlanner.cs](../../AnythingSearch/Services/Indexing/IndexPlanner.cs) | Decides *what* is indexed, in what order, in what units. |
| [IndexPlanner.SystemDrive.cs](../../AnythingSearch/Services/Indexing/IndexPlanner.SystemDrive.cs) | Splits the OS drive into sub-phases. |
| [IndexingCapacity.cs](../../AnythingSearch/Services/Indexing/IndexingCapacity.cs) | Chooses walker count and throttle per drive. |
| [StorageProfiler.cs](../../AnythingSearch/Services/Indexing/StorageProfiler.cs) | Identifies the storage device behind a drive letter. |
| [IndexingState.cs](../../AnythingSearch/Models/IndexingState.cs) | `indexing_state.json` — the resumable checkpoint file. |
| [DatabaseStatus.cs](../../AnythingSearch/Models/DatabaseStatus.cs) | `database_status.json` — display/status only. |

---

## 4. The three phases

Defined in `IndexPlanner.BuildScopes()` ([IndexPlanner.cs:64](../../AnythingSearch/Services/Indexing/IndexPlanner.cs)):

```
Phase 1  Priority     "Downloads"                       -> 1 scope
Phase 2  DataDrives   every ready, fixed, non-OS drive  -> 1 scope per drive
Phase 3  SystemDrive  the OS drive, split into sub-phases:
                        key                            label
                        os:C:\:Users                   C:\Users
                        os:C:\:Program Files (x86)     C:\Program Files (x86)
                        os:C:\:Program Files           C:\Program Files
                        os:C:\:Windows                 C:\Windows
                        os:C:\:*rest                   C:\ (remaining folders)
                                                       <- everything not claimed above
```

The key uses `Path.GetFileName(root)`; the label is the full directory path.

Phase 3 is only added when `AppSettings.IndexSystemDrive` is `true` (default).

The OS sub-phase directories are **resolved from Windows special folders**, not hard-coded —
`Environment.SpecialFolder.UserProfile`'s parent, `ProgramFilesX86`, the `ProgramW6432`
environment variable, `ProgramFiles`, `Windows`. Each candidate must be a *top-level* directory
of the OS drive, must exist, must not be excluded, and duplicates are dropped. The final
`*rest` sub-phase always covers the remainder, so nothing is missed on an unusual layout.

Scope keys (`phase1:priority`, `drive:D:\`, `os:C:\:Users`, …) are **stable across runs**,
which is what makes resume work.

---

## 5. End-to-end flow

```
 MainForm ctor
     |
     +-> new FileDatabase()  -> new SearchManager(db) -> new BackgroundIndexingService(db)
     |
     v
 MainForm.InitializeAsync()                       [MainForm.cs:117]
     |
     v
 SearchManager.InitializeAsync()                  [SearchManager.cs:106]
     |
     v
 BackgroundIndexingService.InitializeAsync()      [BackgroundIndexingService.cs:121]
     |
     +-- await _database.InitializeAsync()          (open + CREATE TABLE + PRAGMAs)
     +-- count = await _database.GetCountAsync()
     |
     +-- count == 0  AND  state has scopes?  --> _state.Reset()   (state/db disagree)
     |
     +-- SyncPlanWithState()   (drop scopes for removed drives, add new ones, save)
     |
     +-- state.IsComplete && count > 0 ?
     |        yes -> _hasSearchableData = true
     |                MarkCompleted, fire DatabaseReady, LegacyDataCleanup, RETURN
     |
     +-- state.HasSearchableData && count > 0 ?
     |        yes -> _hasSearchableData = true, fire DatabaseReady   (partial index usable now)
     |
     +-- StartPipeline(fullRebuild: false)
              |
              v
     Task.Run(RunPipelineAsync)   <-- returns immediately; UI thread is free
```

`InitializeAsync` has a `forceReindex` parameter, but **no production caller passes `true`** —
`SearchManager.InitializeAsync` calls it with the default. Rebuild goes through
`RebuildIndexAsync` instead (see [02-Rebuild-Indexing.md](02-Rebuild-Indexing.md)).

---

## 6. The pipeline loop

`RunPipelineAsync` ([BackgroundIndexingService.Pipeline.cs:44](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.Pipeline.cs)):

```
RunPipelineAsync(fullRebuild, token)
  |
  +-- if fullRebuild: _hasSearchableData = false; state.Reset(); await db.ClearAsync()
  +-- _status.MarkIndexingStarted()
  +-- await db.PrepareForBulkIndexingAsync()      (bulk PRAGMAs + UNIQUE index up front)
  +-- scopes = SyncPlanWithState();  _totalScopes = scopes.Count
  +-- restore _completedScopes / _totalFiles / _totalFolders from state
  |
  +-- FOR EACH scope (strictly one at a time, in plan order)
  |       |
  |       +-- cancelled? -> break
  |       +-- scopeState.Status == Completed? -> skip
  |       +-- await RunScopeAsync(scope, scopeState, token)
  |
  +-- cancelled?  -> MarkFailed("Indexing was cancelled - progress has been saved"); RETURN
  |
  +-- await db.FinalizeIndexingAsync()     (checkpoint, runtime PRAGMAs, ANALYZE, VACUUM, checkpoint)
  +-- state.CompletedAt = now; state.Save()
  +-- _isIndexing = false; _hasSearchableData = true; _status.MarkCompleted(...)
  +-- ReportProgress("Index complete - N items in mm:ss (N/sec)")
  +-- IndexingCompleted?.Invoke();  DatabaseReady?.Invoke()
  +-- Task.Run(LegacyDataCleanup.Run)      (fire and forget)
```

**Error handling:** the whole body is wrapped in
`catch (OperationCanceledException)` → `MarkFailed("… cancelled - progress has been saved")`, and
`catch (Exception ex)` → `MarkFailed(ex.Message)` + `IndexingFailed` event. A `finally` block
always clears `_isIndexing`.

---

## 7. One scope

`RunScopeAsync` ([BackgroundIndexingService.Pipeline.cs:139](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.Pipeline.cs)):

```
RunScopeAsync(scope, scopeState, token)
  |
  +-- _currentPhase = scope.Phase;  reset _scopeFiles/_scopeFolders
  +-- under _stateLock: Status = InProgress, StartedAt ??= now, Error = null,
  |                     capture baseFiles/baseFolders/segmentBase, save state
  +-- ReportProgress("Phase N - indexing <label>...")
  |
  +-- alreadyDone = scopeState.CompletedUnits            <-- the resume checkpoint
  +-- units      = planner.ExpandUnits(scope) MINUS alreadyDone
  +-- skipDirs   = SkipDirectoriesFor(scope)             <-- Downloads, for phase 3
  +-- chunkSize  = IndexingCapacity.WalkersFor(scope.Drive)
  +-- segmentThreshold = SettingsService.Current.LargeScopeSegmentItems   (default 500,000)
  |
  +-- FOR offset = 0; offset < units.Count; offset += chunkSize
  |       chunk = units[offset .. offset+chunkSize]
  |       await RunChunkAsync(chunk, ...)                <-- walk + commit + checkpoint
  |
  |       if segmentThreshold == 0            -> continue
  |       if this was the LAST chunk          -> continue   (scope publishes anyway)
  |       if scopeState.Items - segmentBase < segmentThreshold -> continue
  |       segmentBase = scopeState.Items
  |       await PublishSegmentAsync(scope, scopeState)    <-- mid-scope publish
  |
  +-- cancelled? -> RETURN (scope stays InProgress -> Pending on next load)
  |
  +-- await db.FinalizeScopeAsync()    (PRAGMA wal_checkpoint(TRUNCATE) only — cheap)
  +-- under _stateLock: Status = Completed, CompletedAt = now, Error = null,
  |                     CompletedUnits.Clear(), save
  +-- _completedScopes++
  |
  +-- scopeState.Items == 0 && !_hasSearchableData ?
  |        yes -> ReportProgress("<label>: nothing to index"); RETURN   (no publish)
  |
  +-- _hasSearchableData = true
  +-- ReportProgress("<label> indexed - N items, now searchable")
  +-- ScopePublished?.Invoke(label)
```

The empty-scope guard matters: a machine with no Downloads folder must not unlock search on a
scope that produced nothing.

### How units are built

`IndexPlanner.ExpandUnits` → `AddSplitUnits` ([IndexPlanner.cs:177](../../AnythingSearch/Services/Indexing/IndexPlanner.cs)):

```
AddSplitUnits(dir, units, depthBudget = 2)
  subDirs = dir.GetDirectories() minus excluded/skippable
  |
  +-- subDirs empty OR units.Count >= 2000 (MaxUnitsPerScope)
  |        -> units += ScanRoot(dir, Recursive: true)
  |
  +-- subDirs.Count > 4 (SplitThreshold)
  |        -> units += ScanRoot(sub, recursive) for each sub
  |           units += ScanRoot(dir, Recursive: false)   <- dir's own files only
  |
  +-- depthBudget <= 0
  |        -> units += ScanRoot(dir, Recursive: true)
  |
  +-- else (narrow directory, budget left)
           -> recurse into each sub with depthBudget - 1
              units += ScanRoot(dir, Recursive: false)
```

`Deduplicate()` then drops any unit that already sits inside a recursive unit, so no subtree is
walked twice. `IsExcluded` (folder names from `AppSettings.ExcludedFolders`) and `IsSkippable`
(reparse points; system **and** hidden) are applied throughout.

---

## 8. One chunk — the producer/consumer pipeline

`RunChunkAsync` ([BackgroundIndexingService.Pipeline.cs:239](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.Pipeline.cs)) creates a **fresh bounded channel per chunk**:

```
                 chunk = N units  (N = walker count for this drive)

   Parallel.ForEach(chunk, MaxDegreeOfParallelism = chunk.Count)
   +-----------+   +-----------+   +-----------+
   | ScanUnit  |   | ScanUnit  |   | ScanUnit  |     <- producers (threads)
   +-----+-----+   +-----+-----+   +-----+-----+
         |               |               |
         |  Write(FileEntry) - blocks when full (back-pressure)
         v               v               v
   +-------------------------------------------------+
   |  Channel<FileEntry>  bounded 50,000              |
   |  FullMode = Wait, SingleReader, !SingleWriter    |
   +-------------------------+-----------------------+
                             |
                             v
                   ConsumerAsync()  (single Task)
                   batches up to 10,000 entries
                             |
                             v
                   db.InsertAsync(entry) per entry
                   -> ConcurrentBag, flushed at 10,000
                             |
                             v
                   SQLite, inside ONE transaction per chunk
```

Sequence inside `RunChunkAsync`:

```
1.  _channel = Channel.CreateBounded<FileEntry>(50_000)
2.  await _database.BeginBatchAsync()                 <- BEGIN TRANSACTION + prepared statements
3.  consumer = Task.Run(ConsumerAsync)                <- starts draining
4.  await Task.Run(() => Parallel.ForEach(chunk, ..., ScanUnit))
       - token checked INSIDE the body, not via ParallelOptions (an orderly stop, not a throw)
       - per-unit exceptions collected into a ConcurrentBag<string> `failures`
5.  finally:
       _channel.Writer.TryComplete()
       await consumer                                  <- drains everything already produced
       await _database.CommitBatchAsync()              <- flush + COMMIT, even when cancelled
6.  under _stateLock:
       scopeState.Files/Folders  = base + Interlocked.Read(_scopeFiles/_scopeFolders)
       failures merged into scopeState.FailedUnits, Error set if any
       if NOT cancelled: CompletedUnits += every unit key;  LastCheckpoint = last dir
       _state.Save()
7.  ReportProgress()
```

**The commit/checkpoint pairing is the durability contract**: the state file only records a
unit as complete after the transaction holding its rows has committed. A chunk cancelled
part-way is *not* checkpointed, so it is re-walked next run — and `INSERT OR IGNORE` against
the `UNIQUE(FolderId, Name)` index absorbs the repeats (see
[04-Database-And-Index-Writing.md](04-Database-And-Index-Writing.md)).

### The walk itself

`ScanUnit` ([BackgroundIndexingService.DirectoryScanning.cs:21](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.DirectoryScanning.cs)) — iterative, with an explicit `Stack<DirectoryInfo>`:

```
ScanUnit(root, skipDirectories, token)
  if IndexPlanner.IsSkippable(root.Directory) -> return   (junction handed out as a unit root)
  (throttleBatch, throttleDelay) = IndexingCapacity.ThrottleFor(root path)
  stack.Push(root.Directory)

  while stack not empty && !cancelled:
      dir = stack.Pop()
      if planner.IsExcluded(dir) -> continue
      if dir in skipDirectories  -> continue
      _currentPath = dir.FullName                  <- what the progress label shows

      if dir.Parent != null:                       <- drive roots get no row of their own
          Write(folder FileEntry)                  <- Modified = SafeLastWrite(dir)
          Interlocked.Increment(_scopeFolders, _totalFolders)
          Throttle()

      ScanFiles(dir)                               <- files directly inside
          per file: skip excluded extension, Write(file FileEntry), counters, Throttle()
          enumerator MoveNext() wrapped in try/catch -> break on failure
          per-file body wrapped in try/catch -> swallowed (except OperationCanceledException)

      if !root.Recursive -> continue                <- files-only unit stops here
      PushSubdirectories(dir)                       <- skippable/excluded/skipped filtered out
```

`Write` ([:165](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.DirectoryScanning.cs)) spins on `TryWrite`, then blocks on
`WaitToWriteAsync(...).GetAwaiter().GetResult()` — that block **is** the back-pressure that keeps
walkers from outrunning the single SQLite writer.

`Throttle` sleeps `throttleDelay` ms every `throttleBatch` entries; a delay of `0` disables it.

---

## 9. Per-drive pacing

`IndexingCapacity` ([IndexingCapacity.cs](../../AnythingSearch/Services/Indexing/IndexingCapacity.cs)) chooses both the walker count (= chunk size)
and the throttle, **per drive**, because scopes are per drive.

```
WalkersFor(path)
  |
  +-- AutoTuneIndexing == false ? -> max(1, settings.MaxIndexingThreads)   [verbatim, no tuning]
  |
  +-- media = StorageProfiler.Profile(path).Media
  +-- walkers = Nvme -> 4 | SolidState -> 4 | (Rotational/Removable/Unknown) -> 2
  |
  +-- floor = ProcessorCount <= 1 ? 1 : 2
  +-- cpuLimit = max(floor, ProcessorCount - 1)     -> one core left for writer + UI
  +-- walkers > cpuLimit          -> walkers = cpuLimit
  +-- GC total available RAM < 4 GiB && walkers > floor  -> walkers = floor
  +-- on battery && walkers > floor                     -> walkers = max(floor, walkers / 2)
  +-- Explain(path, ...) -> Logger.Log, once per drive root
```

```
ThrottleFor(path)
  +-- AutoTuneIndexing == false ? -> (IndexThrottleBatchSize, IndexThrottleDelayMs) from settings
  +-- on battery                 ? -> (2000, 4)   [the Unknown profile — full pause kept]
  +-- Nvme        -> (8000, 0)   no pause
  +-- SolidState  -> (4000, 2)
  +-- otherwise   -> (2000, 4)
```

`StorageProfiler.Profile` ([StorageProfiler.cs:57](../../AnythingSearch/Services/Indexing/StorageProfiler.cs)) caches per drive letter for the life of the
process. It opens `\\.\X:` with **zero desired access** (works without elevation), then:

```
DriveType == Removable            -> Removable
bus == NVMe (0x11)                -> Nvme
bus in {USB 0x07, SD 0x0C, MMC 0x0D} -> Removable
IOCTL seek-penalty == true        -> Rotational
IOCTL seek-penalty == false       -> SolidState
anything else / any exception     -> Unknown   (paced exactly as before auto-tuning existed)
```

Every limit stops at the historic default of 2 walkers, so auto-tuning can only make a machine
faster, never slower.

---

## 10. Mid-scope publishing (segments)

The OS drive is divided in advance because Windows tells us where its big directories are. A
data drive gives nothing to go on, so it is divided **by what has actually been indexed**.

`BackgroundIndexingService.Segments.cs` → `PublishSegmentAsync`:

```
after a chunk commits, if (scopeState.Items - segmentBase) >= LargeScopeSegmentItems
and there are still chunks left:
     await db.FinalizeScopeAsync()             <- fold WAL back in first
     scopeState.Segments += { Index, Items, LastUnit = LastCheckpoint, CompletedAt }
     _state.Save()
     _hasSearchableData = true
     ScopePublished?.Invoke("Drive D:\ (part 2)")
```

Segments are **publish points, not checkpoints** — resume is still driven by
`CompletedUnits`, so a segment boundary costs nothing if the app closes mid-scope.

---

## 11. Persisted state

### `indexing_state.json` — [IndexingState.cs](../../AnythingSearch/Models/IndexingState.cs)

Written next to the database, in `ApplicationDataManager.Instance.ApplicationDataDirectory`.

```
IndexingState
  SchemaVersion = 1    StartedAt   CompletedAt   LastUpdatedAt
  Scopes: [ IndexScopeState
              Key            "drive:D:\"
              Phase          DataDrives
              Drive          "D:\"
              Status         Pending | InProgress | Completed | Failed
              StartedAt / CompletedAt
              Files / Folders           (Items => Files + Folders)
              LastCheckpoint            last unit committed, display only
              CompletedUnits: [ "R|D:\Projects", "F|D:\", ... ]   <- RESUME DRIVES OFF THIS
              Error / FailedUnits
              Segments: [ IndexSegmentState ]
          ]
  IsComplete        => every scope Completed
  HasSearchableData => any  scope Completed
```

`Load()` performs one repair: any scope left `InProgress` (the app died mid-scope) is put back
to `Pending`. Its `CompletedUnits` are still valid, so it resumes from them. A corrupt or
unreadable file is logged and a fresh plan is started.

`Save()` is called under `_stateLock` at every scope transition and after every chunk.

### `database_status.json` — [DatabaseStatus.cs](../../AnythingSearch/Models/DatabaseStatus.cs)

Display and diagnostics only; **resumability does not depend on it**. `UpdateProgress` writes at
most once every 5 seconds (`SaveInterval`). `Load()` converts a stale `Indexing` state into
`Failed` with "Indexing was interrupted (application closed unexpectedly)".

---

## 12. Events and threading

| Event | Raised from | Consumer |
| --- | --- | --- |
| `ProgressChanged(IndexProgress)` | `ReportProgress` (pipeline/consumer threads) | `SearchManager.OnIndexingProgress`, `MainForm.OnIndexingProgress` |
| `ScopePublished(string)` | end of `RunScopeAsync` / `PublishSegmentAsync` | `SearchManager.OnScopePublished` → `MemorySearchService.RequestRebuild`; `MainForm.OnScopePublished` |
| `DatabaseReady` | `InitializeAsync` and end of `RunPipelineAsync` | `SearchManager.OnDatabaseReady` |
| `IndexingCompleted` | end of `RunPipelineAsync` | `MainForm.OnIndexingCompleted` |
| `IndexingFailed(string)` | `RunPipelineAsync` catch block | `SearchManager.OnIndexingFailed` |
| `CatchUpStatusChanged(string)` | catch-up pass | `MainForm.OnWatcherStatus` |

All of these fire on **background threads**. Every UI handler in `MainForm` wraps its body in
`SafeInvoke`, which marshals to the UI thread and swallows exceptions if the form is disposing.

Threading summary:

* `StartPipeline` uses `Task.Run(..., CancellationToken.None)` — the pipeline task itself is
  never cancelled at scheduling time; the token is checked inside.
* Counters (`_totalFiles`, `_scopeFolders`, …) are `long` fields updated with `Interlocked`.
* `_state` is mutated from parallel walkers and the pipeline, always under `_stateLock`.
* `_isIndexing`, `_hasSearchableData`, `_phaseLabel`, `_currentPhase` are `volatile`.
* `_currentPath` is a plain `string` field written by walkers and read by the progress
  reporter — a torn read is not possible for a reference assignment, and it is display-only.

---

## 13. Cancellation and shutdown

```
CancelIndexing()          -> cts.Cancel() + channel.Writer.TryComplete()   [does not wait]

StopAsync(timeout)        -> CancelIndexing(), then Task.WhenAny(pipelineTask, Task.Delay(timeout))
                             - completed  -> await the task so faults are observed; true
                             - timed out  -> log "Indexing shutdown timed out"; false
                             Deliberately writes no status: the pipeline's own cancellation
                             branch records "cancelled - progress has been saved".

Dispose()                 -> best effort only: cancel, complete the channel, MarkFailed
                             ("Application closed during indexing"). Cannot wait.
```

`MainForm.ShutdownServicesAsync` ([MainForm.Events.cs:68](../../AnythingSearch/Forms/MainForm.Events.cs)) drives the orderly path with a
**single 10-second budget shared** between the watcher and the indexer, watcher first (it is the
one writing single entries), then `SearchManager.ShutdownAsync` → `StopAsync`. The form's
`FormClosing` cancels the first close, hides the window, drains, then calls `Close()` again —
a synchronous wait would deadlock against the progress callbacks that use `Invoke`.

---

## 14. Performance considerations and bottlenecks

**By design**

* One scope at a time. Never two drives at once.
* Walker count capped at 4 — measured on an SSD, concurrency stops paying above that
  (1→32 k/s, 2→38.5 k/s, 4→61.3 k/s, 8→61.1 k/s, 12→62.2 k/s items/sec; see the table in
  [IndexingCapacity.cs:52](../../AnythingSearch/Services/Indexing/IndexingCapacity.cs)).
* Channel bounded at 50,000 (down from a historic 200,000) — it is a buffer, not a staging area.
* Throttle pause keeps the machine usable on mechanical disks.

**Known cost centres**

| Where | Cost |
| --- | --- |
| Single SQLite consumer per chunk | The one writer is a hard ceiling. Measured at ~276,000 rows/sec against a whole-pipeline rate of ~21,000 items/sec with 2 walkers, so it has an order of magnitude of headroom today — but it *is* the ceiling if the writer is ever made slower. |
| `FileDatabase.InsertAsync` | Reads `ConcurrentBag.Count` on **every** entry to decide whether to flush. `ConcurrentBag<T>.Count` is not a cached counter. |
| `_state.Save()` | Serializes the whole state file (indented JSON, including every `CompletedUnits` path) after every chunk. Bounded by `MaxUnitsPerScope = 2000`. |
| `IndexPlanner.IsExcluded` | Called per directory; loops the ~28-entry `ExcludedFolders` list doing two `Contains`/`EndsWith` per entry. |
| Planning depth | `AddSplitUnits` costs one directory read per level; capped by `SplitThreshold = 4`, `ExtraSplitDepth = 2` and `MaxUnitsPerScope = 2000`. |
| `FinalizeIndexingAsync` | `ANALYZE` + `VACUUM` + two `wal_checkpoint(TRUNCATE)` at the very end of a full build — the single most expensive step, which is why per-scope wrap-up only checkpoints. |

**Correctness guards that also protect performance**

* `IsSkippable` rejects reparse points — a self-referencing junction would otherwise never
  terminate, and following any junction duplicates a tree the unique index cannot catch
  (the paths differ).
* `SkipDirectoriesFor` keeps phase 3 from re-walking Downloads — but only when phase 1 actually
  *completed*, otherwise the OS-drive pass is the only thing that will finish it.

---

## 15. Interaction map

```
        MainForm
           | events (SafeInvoke)
           v
      SearchManager  ------------------------------+
           |                                       |
           | owns                                  | RequestRebuild(reason)
           v                                       v
  BackgroundIndexingService  --ScopePublished-->  MemorySearchService
           |                                       |
           | IndexPlanner (what/where)             | MemoryIndexBuilder
           | IndexingCapacity (how hard)           |   (own read-only connection)
           | IndexingState   (resume)              v
           |                                  MemoryFileIndex (immutable snapshot)
           v
      FileDatabase  <---- FileWatcherService (incremental writes, see 05)
           |
           v
   AnythingSearch.db (+ -wal, -shm)
```
