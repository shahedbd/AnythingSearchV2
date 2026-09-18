# 05 — Auto Watch / File System Monitoring

Keeping the index current after the initial build: live `FileSystemWatcher` monitoring while the
app runs, and the catch-up pass that reconciles what happened while it did not.

> **Related documents**
> [01-Indexing.md](01-Indexing.md) ·
> [02-Rebuild-Indexing.md](02-Rebuild-Indexing.md) ·
> [03-File-Search.md](03-File-Search.md) ·
> [04-Database-And-Index-Writing.md](04-Database-And-Index-Writing.md)

---

## 1. Purpose

Two complementary mechanisms:

| Mechanism | Covers | Runs |
| --- | --- | --- |
| **Auto-watch** (`FileWatcherService`) | Changes happening **now**, while the app is running. | Continuously, in 3-second batches. |
| **Catch-up pass** (`BackgroundIndexingService.CatchUp`) | Changes that happened while the app was **closed**, and anything lost to a watcher buffer overflow. | Once at startup, in the background. |

Both write to the same SQLite store the search box falls back to, and both mirror into the same
in-memory overlay every search scans — so **the governing constraint is that neither may slow
search down**. Every path is bounded: a fixed amount of indexing work per batch, no unbounded
re-walks, and no allocation on the OS callback thread.

---

## 2. Key components

| File | Responsibility |
| --- | --- |
| [FileWatcherService.cs](../../AnythingSearch/Services/Indexing/FileWatcherService.cs) | Watcher lifecycle, raw OS event wiring, error recovery, shutdown. |
| [FileWatcherService.ChangeQueue.cs](../../AnythingSearch/Services/Indexing/FileWatcherService.ChangeQueue.cs) | Debounce/batch queue and batch application. |
| [FileWatcherService.SyncHandlers.cs](../../AnythingSearch/Services/Indexing/FileWatcherService.SyncHandlers.cs) | Per-change-type database sync. |
| [FileWatcherService.DirectoryWalk.cs](../../AnythingSearch/Services/Indexing/FileWatcherService.DirectoryWalk.cs) | Budgeted walk of a newly created folder. |
| [FileWatcherService.Filters.cs](../../AnythingSearch/Services/Indexing/FileWatcherService.Filters.cs) | Allocation-free ignore list. |
| [BackgroundIndexingService.CatchUp.cs](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.CatchUp.cs) | Startup reconciliation. |
| [FileSystemChange.cs](../../AnythingSearch/Models/FileSystemChange.cs) | One queued change. |

---

## 3. Watcher lifecycle

```
MainForm ctor
   _fileWatcher = new FileWatcherService(_database)
   _fileWatcher.AttachMemoryIndex(_searchManager.MemoryIndex)   <- mirror target

StartFileWatcher()                                  [MainForm.Indexing.cs:234]
   |
   +-- _searchManager.IsIndexing ?
   |       -> "Auto-watch: Will start after indexing..."   RETURN
   +-- !_searchManager.IsDatabaseReady ?
   |       -> "Auto-watch: Waiting for database..."        RETURN
   +-- _fileWatcher.StartWatching()
   +-- lblWatchStatus = "Auto-watch: Monitoring file changes"
```

Triggered from: `MainForm.InitializeAsync` (if ready and the checkbox is on),
`OnSearchSourceChanged`, `OnIndexingCompleted`, and the `chkAutoWatch` checkbox.
Stopped by `BtnIndex_Click` before any build — it writes to the same database.

```
StartWatching()                                     [FileWatcherService.cs:74]
  lock (_lock)
    if _isRunning -> return
    _isRunning = true
    drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == Fixed)
    foreach drive: CreateWatcher(drive.RootDirectory.FullName)   -> _watchers
                   (per-drive try/catch; a failure reports and continues)
    _processTimer.Change(3000, 3000)        <- ProcessIntervalMs, repeating
    StatusChanged("✓ Monitoring N drive(s) for changes")
```

```
CreateWatcher(path)
  new FileSystemWatcher {
      Path = path,
      IncludeSubdirectories = true,
      NotifyFilter = FileName | DirectoryName | LastWrite | Size | CreationTime,
      Filter = "*.*",
      InternalBufferSize = 65536        // 64 KB, default is 8 KB
  }
  += Created, Deleted, Renamed, Changed, Error
  EnableRaisingEvents = true
  (returns null on any exception)
```

Note the watcher set is built from the **drive list**, using `DriveInfo` directly — it does not
go through `IndexPlanner`, so the same fixed drives are watched regardless of which scopes the
planner produced.

### Error recovery

```
OnError(sender, e)
   StatusChanged("⚠ Watcher error: <message>")
   Task.Run: await Task.Delay(5000); RestartWatcher(failedWatcher)

RestartWatcher(failed)
   lock (_lock)
     if !_isRunning -> return
     path = failed.Path
     _watchers.Remove(failed); failed.Dispose()
     newWatcher = CreateWatcher(path)
     if newWatcher != null -> _watchers.Add(...); "✓ Restarted monitoring: <path>"
```

`OnError` is what Windows raises when the kernel buffer overflows — the events in it are gone.
That gap is the catch-up pass's job on the next launch (section 7).

---

## 4. The queue

```
  FileSystemWatcher callback thread          Timer thread (every 3 s) / drain thread
            |                                              |
            v                                              v
       QueueChange(path, type, oldPath?)          ProcessChangesAsync(flushAll?)
            |                                              |
            +-- !_isRunning -> return                      +-- _stopping -> return
            +-- ShouldIgnore(path) -> return               +-- queue empty && no backlog -> return
            +-- build FileSystemChange                     +-- Interlocked claim _processing
            |     Timestamp = UtcNow  (latest event)       +-- ResetWalkBudget()  (4000)
            |     FirstSeen = UtcNow  (first enqueue)      +-- batch = TakeBatch(flushAll)
            v                                              +-- ApplyChangesAsync(batch)
   _pendingChanges : ConcurrentDictionary<string, FileSystemChange>
   _pendingCount   : int, Interlocked
```

`QueueChange` runs on the **FileSystemWatcher callback thread**, which has a hard deadline:
Windows hands events out of a fixed kernel buffer, and anything still unread when the buffer fills
is thrown away and reported as an overflow — costing the whole subtree until the next catch-up
pass. So this path avoids every allocation it can, and **nothing here calls
`ConcurrentDictionary.Count`** (that property takes every one of the dictionary's internal locks).
`_pendingCount` tracks the size instead.

### Deduplication rules — [FileWatcherService.ChangeQueue.cs:41](../../AnythingSearch/Services/Indexing/FileWatcherService.ChangeQueue.cs)

```
while (true):
    if _pendingChanges.TryGetValue(path, out existing):
        if type == Modified && existing.Type != Modified:
            existing.Timestamp = now                  <- a plain Modified must NOT erase a
            pending = _pendingCount; break               queued Created/Deleted/Renamed
        change.FirstSeen = existing.FirstSeen         <- inherit the original enqueue time
        if !_pendingChanges.TryUpdate(path, change, existing) continue;   <- lost a race, retry
        pending = _pendingCount; break
    if _pendingChanges.TryAdd(path, change):
        pending = Interlocked.Increment(ref _pendingCount); break
```

* **The newest event wins.** Keeping an older `Deleted` over a newer `Created` used to drop files
  saved atomically (write temp → delete target → rename), which is how most editors and git write
  files.
* Spelled out as `TryGetValue`/`TryUpdate`/`TryAdd` rather than `AddOrUpdate` so every change in
  the queue's **size** goes through exactly one atomic operation the counter can be paired with.
  `AddOrUpdate` can run its add factory and then discard the result after losing a race, which
  would leave `_pendingCount` drifting further from the truth with every event.

### High-water-mark drain

```
MaxPendingChanges = 10000
HighWaterMark     = 5000

if pending >= HighWaterMark && Interlocked.CompareExchange(_drainScheduled, 1, 0) == 0:
    flushAll = pending >= MaxPendingChanges
    Task.Run(async () => { try { await ProcessChangesAsync(flushAll); }
                           finally { _drainScheduled = 0; } })
```

`_drainScheduled` exists because without it every single event arriving above the mark queued
another thread-pool item, and all but one existed only to lose the race for the batch.

---

## 5. Batch processing

```
ProcessIntervalMs = 3000     timer period
DebounceMs        = 500      a path must be quiet this long
MaxChangeAgeMs    = 10000    ... or have been waiting this long overall
```

```
ProcessChangesAsync(flushAll = false)
  |
  +-- _stopping ? -> RETURN                     <- the SINGLE place a batch is claimed, so also
  |                                                the single place that has to refuse
  +-- queue empty && no deferred dirs ? -> RETURN
  +-- Interlocked.CompareExchange(_processing, 1, 0) != 0 ? -> RETURN
  |      (the check-then-set this replaced let the 3 s timer and the drain both get past it,
  |       so two batches applied the same changes at once)
  |
  +-- ResetWalkBudget()                         <- 4000 entries for this whole batch
  +-- sortedChanges = TakeBatch(flushAll)
  +-- nothing new && no backlog ? -> RETURN
  +-- (processed, errors) = await ApplyChangesAsync(sortedChanges)
  +-- StatusChanged("Auto-watch: N change(s) synced" | "... N change(s), M error(s)")
  |
  +-- catch -> StatusChanged("Auto-watch: Error processing changes: ...")
  +-- finally -> _processing = 0
```

```
TakeBatch(flushAll)
  quietCutoff = UtcNow - 500 ms
  ageCutoff   = UtcNow - 10 s
  foreach kvp in _pendingChanges:
      if !flushAll && change.Timestamp >= quietCutoff && change.FirstSeen >= ageCutoff -> skip
      if _pendingChanges.TryRemove(kvp): _pendingCount--; batch.Add(change)
  batch.Sort(by Rank)      Deleted=0, Created=1, Renamed=2, Modified=3
  return batch
```

Two rules keep the queue from silently losing changes:

1. A queued path is flushed once it has been quiet for `DebounceMs` **or** once it has been
   waiting for `MaxChangeAgeMs` — a path that keeps receiving events (a log file, a download in
   progress) can never starve the queue.
2. When the queue is full it is drained immediately, ignoring the debounce, rather than dropping
   the incoming change.

The sort is in place rather than via `OrderBy` — this runs on every batch, and the old
`Select/Where/ToList/OrderBy/ToList` chain built three copies of the list to no purpose.

```
ApplyChangesAsync(sortedChanges)
  await db.BeginIncrementalTransactionAsync()          <- ONE transaction for the batch
  try:
      await DrainDeferredDirectoriesAsync()            <- BEFORE the batch's own changes
      foreach change (in Deleted, Created, Renamed, Modified order):
          if _stopping -> break
          try: dispatch to the handler; processed++
          catch: Logger.Log("File watcher could not apply a change to <path>: ..."); errors++
  finally:
      await db.CommitIncrementalTransactionAsync()
```

The backlog drains **first**, not last: queued behind whatever arrived in the last three seconds
on a busy machine, it would never drain at all.

Per-change exceptions are caught individually — one bad path never aborts a batch, and the
transaction still commits.

---

## 6. Change handlers

[FileWatcherService.SyncHandlers.cs](../../AnythingSearch/Services/Indexing/FileWatcherService.SyncHandlers.cs). **Every handler resolves the path against the
real file system before writing.** OS events are only a hint that "something happened here" —
they arrive out of order, are coalesced by Windows, and are lost on buffer overflow, so the event
type alone must never decide whether a row is inserted or deleted.

```
HandleCreatedAsync(path)
  |
  +-- Directory.Exists(path) ?
  |      IndexPlanner.IsSkippable(dir) -> RETURN          <- `mklink /J` raises Created like any
  |                                                          other new directory; skipped whole,
  |                                                          row included, matching ScanUnit
  |      await db.ExistsAsync(path)   -> RETURN           <<< SEE BELOW
  |      db.InsertSingleAsync(folder entry); memory.NotifyAdded(entry)
  |      await IndexNewDirectoryAsync(dir)                <- budgeted walk
  |
  +-- File.Exists(path) ?
         IsExcludedExtension(file.Extension) -> RETURN
         await db.ExistsAsync(path)          -> RETURN
         db.InsertSingleAsync(file entry); memory.NotifyAdded(entry)
```

> **The `ExistsAsync` early return is the single most important thing this class does for search
> latency.** A directory raises a `Changed` event of its own every time a file is added to or
> removed from it, and `HandleModifiedAsync` routes those back into `HandleCreatedAsync`. Without
> the guard, saving one file in a large folder re-walked that folder's **entire subtree** — a
> database round trip per file, each taking the connection gate the search box also queues behind.
> A busy disk kept that walk running permanently, which is how a five-character query ended up
> waiting 87 seconds.
>
> Nothing is lost by skipping: entries inside an already-indexed directory raise their own events,
> and anything that happened while the app was closed belongs to the catch-up pass.

```
HandleDeletedAsync(path)
  if File.Exists(path) || Directory.Exists(path) -> HandleCreatedAsync(path); RETURN
       (atomic saves and fast delete/recreate cycles produce a Delete for a path that
        exists again by the time we get here)
  await db.DeleteByPathAsync(path)         <- also sweeps the subtree if it was a folder
  memory.NotifyRemoved(path)

HandleRenamedAsync(oldPath, newPath)
  updated = await db.UpdatePathAsync(oldPath, newPath)
  if updated == 0 -> HandleCreatedAsync(newPath); RETURN
       (the old path was never indexed: created while the app was closed, or lost to an overflow)
  memory.NotifyRemoved(oldPath)
  memory.NotifyAdded(<file or directory entry for newPath>)

HandleModifiedAsync(path)
  if !File.Exists(path):
      if Directory.Exists(path) -> HandleCreatedAsync(path)    <- a directory whose contents
      RETURN                                                      changed raises Modified on itself
  if await db.ExistsAsync(path):
      db.UpdateFileAsync(entry); memory.NotifyUpdated(entry)
  else:
      HandleCreatedAsync(path)                                 <- missed the create event
```

Note `HandleRenamedAsync` updates SQLite for a renamed **folder**'s own row, but does not rewrite
the folder-path rows of its descendants; those are reconciled by the catch-up pass.

---

## 7. The budgeted directory walk

[FileWatcherService.DirectoryWalk.cs](../../AnythingSearch/Services/Indexing/FileWatcherService.DirectoryWalk.cs). This is the incremental counterpart to the bulk
walk in `BackgroundIndexingService.DirectoryScanning.cs`. The two are deliberately separate — the
bulk one streams into a channel for a batched writer, this one writes single entries and mirrors
each into the overlay — but they **must agree on which directories belong in the index**, which is
why both go through `IndexPlanner.IsSkippable`.

```
WalkBudgetPerBatch     = 4000      entries one batch may index from new directories
MaxWatchIndexDepth     = 64        backstop against a redirector that loops without reporting
                                   FileAttributes.ReparsePoint
MaxDeferredDirectories = 20000     cap on the backlog
```

```
WalkAsync(root, startDepth)
  pending = Stack<(DirectoryInfo, int)>;  push (root, startDepth)

  while pending not empty:
      if _stopping -> RETURN
      if _walkBudget <= 0 -> Defer(pending); RETURN

      (dir, depth) = pending.Pop()

      if !await IndexFilesInAsync(dir):          <- ran out mid-directory
          pending.Push((dir, depth))             <- put it BACK before deferring; INSERT OR IGNORE
          Defer(pending); RETURN                    makes re-walking what it wrote free

      if depth >= 64 -> Logger.Log("Auto-watch stopped at depth 64: ..."); continue
      await PushSubdirectoriesAsync(dir, depth, pending)
```

```
Defer(pending)
  foreach (dir, depth) in pending:
      if _deferredDirectories.Count >= 20000:
          Logger.Log("Auto-watch backlog full - <path> left to the next catch-up pass.")
          RETURN
      _deferredDirectories.Enqueue((dir.FullName, depth))
```

```
DrainDeferredDirectoriesAsync()      (called at the top of ApplyChangesAsync)
  while backlog not empty && _walkBudget > 0 && !_stopping:
      (path, depth) = Dequeue()
      if !Directory.Exists(path) -> continue      <- a temp tree, an aborted install
      await WalkAsync(new DirectoryInfo(path), depth)
```

```
IndexFilesInAsync(dir)  -> false if it stopped early
  foreach file in dir.EnumerateFiles():
      if _stopping || _walkBudget <= 0 -> return false
      skip excluded extension / ShouldIgnore
      db.InsertSingleAsync(entry); memory.NotifyAdded(entry); _walkBudget--
      (per-file try/catch swallowed)
  (enumeration itself wrapped in try/catch - access denied, or the directory disappearing mid-walk)
  return true

PushSubdirectoriesAsync(dir, depth, pending)
  foreach sub in dir.EnumerateDirectories():
      if _stopping -> return
      if IndexPlanner.IsSkippable(sub) -> continue      <- junctions, symlinks, system+hidden
      if ShouldIgnore(sub.FullName)    -> continue
      db.InsertSingleAsync(folder entry); memory.NotifyAdded(entry); _walkBudget--
      pending.Push((sub, depth + 1))
```

> **Why an explicit stack, not recursion.** This used to call itself for every subdirectory with
> no guard of any kind. A directory junction pointing at one of its own ancestors (`mklink /J`
> makes one in seconds, and `C:\Users` ships several) never terminated; being *async* recursion,
> each level cost a frame plus a state machine, so it ended in `StackOverflowException` — which
> cannot be caught and takes the process with it. Short of a loop, following any junction
> re-indexed a tree already indexed under its real path, inflating the database and the overlay
> with duplicates the unique `(FolderId, Name)` index cannot catch, because the paths differ.

`IndexFilesInAsync` does **no** per-file existence check: it only runs for directories that were
not in the index a moment ago, and `INSERT OR IGNORE` already rejects a repeat. Asking first
doubled the round trips through the shared connection to establish what the insert settles anyway.

The budget is what turns "a 400,000-file folder was just restored" into work absorbed over a few
minutes instead of search being held up for the duration.

---

## 8. Ignore filters

[FileWatcherService.Filters.cs](../../AnythingSearch/Services/Indexing/FileWatcherService.Filters.cs). `ShouldIgnore` is called on the raw callback thread,
once per OS event, **before anything is queued** — so it must not allocate.

```
EnsureFilters()
  settings = SettingsService.Current
  if ReferenceEquals(_filterSettings, settings) -> return        <- the cache key
  lock (_filterLock):
      double-check
      _excludedSegments[i] = "\<folder>\"      (matches mid-path)
      _excludedSuffixes[i] = "\<folder>"       (matches the path itself)
      _excludedExtensions  = HashSet(settings.ExcludedExtensions, OrdinalIgnoreCase)
      _filterSettings = settings               <- assigned LAST, and volatile, so a reader that
                                                  sees it also sees the arrays
```

`SettingsService` hands out the same `AppSettings` instance until `Reload()`/`ResetToDefaults()`
swaps it, which is what makes reference equality a valid cache key.

The filters used to build two interpolated strings per excluded folder per event: with the ~28
entries in `AppSettings.ExcludedFolders` that is **56 string allocations for every file that
changes anywhere on the machine** — during a build or a large copy, millions of short-lived
strings competing for gen-0 with the search the user is waiting on.

```
ShouldIgnore(path)
  empty -> true
  EnsureFilters()
  for each excluded folder: path.Contains(segment) || path.EndsWith(suffix) -> true
  fileName = Path.GetFileName(path.AsSpan())            <- span, no allocation
  empty -> true
  starts with "~$"           -> true
  ends with ".tmp" / ".temp" -> true
  == "Thumbs.db" / "desktop.ini" -> true
  starts with "AnythingSearch.db" -> true               <- our own database and its -wal/-shm
```

> Names starting with `.` are **not** ignored. That used to hide real content — `.github`,
> `.vscode`, `.env` — from the watcher even though the initial scan indexes them.

---

## 9. The catch-up pass

[BackgroundIndexingService.CatchUp.cs](../../AnythingSearch/Services/Indexing/BackgroundIndexingService.CatchUp.cs). `FileSystemWatcher` only reports changes while the
app is running, and Windows drops events when the buffer overflows — so anything created, renamed
or deleted while the app was closed is invisible to the index until the next full rebuild.

This pass reconciles the index with the disk **without** rebuilding it, using one observation:
*a directory's `LastWriteTime` changes whenever an entry is added to or removed from it*, so only
directories whose timestamp no longer matches the indexed value need to be re-read.

### Trigger

```
MainForm.InitializeAsync()                      [MainForm.cs:154]
  if (_searchManager.IsDatabaseReady && !_searchManager.IsIndexing)
      _ = _searchManager.RunCatchUpAsync()
            .ContinueWith(_ => SafeInvoke(() => _ = UpdateTotalCountAsync()), TaskScheduler.Default)

SearchManager.RunCatchUpAsync(token)            [SearchManager.cs:215]
  => Task.Run(() => _indexingService.RunCatchUpAsync(token), token)
     // thread-pool, not the caller's thread: Microsoft.Data.Sqlite's *Async methods run
     // synchronously, so this would otherwise block the UI at startup
```

### Flow

```
RunCatchUpAsync(token)
  |
  +-- _isCatchingUp || _isIndexing || !_status.IsReady ? -> RETURN
  +-- _isCatchingUp = true
  +-- CatchUpStatusChanged("Checking for changes made while the app was closed...")
  |
  +-- indexedFolders = await db.GetFolderModifiedMapAsync(token)     <- full path -> stored ticks
  +-- roots = _planner.CollectAllUnits()      <- every unit of every scope, deduplicated
  |
  +-- foreach root:
  |       cancelled -> break
  |       try: (a, r) = await CatchUpRootAsync(root, indexedFolders, token); added += a; removed += r
  |       catch: Logger.Log("Catch-up skipped <path>: ...")     <- one unreadable root must never
  |                                                                abort the whole pass
  |       every 20 roots (or once anything changed):
  |           CatchUpStatusChanged("Checking for changes (N/M) - X added, Y removed")
  |
  +-- CatchUpStatusChanged("Index is up to date" | "Index updated: X added, Y removed")
  +-- catch OperationCanceledException -> swallowed
  +-- catch Exception -> CatchUpStatusChanged("Catch-up failed: ...")
  +-- finally -> _isCatchingUp = false
```

```
CatchUpRootAsync(root, indexedFolders, token)     -- iterative, explicit Stack<DirectoryInfo>
  while stack not empty && !cancelled:
      dir = stack.Pop();  path = dir.FullName
      if planner.IsExcluded(path) -> continue

      isDriveRoot = dir.Parent == null
      known       = indexedFolders.TryGetValue(path, out indexedTicks)

      if isDriveRoot || !known || indexedTicks != dir.LastWriteTime.Ticks:
                                              ^^^ THE TIMESTAMP TEST — everything else is skipped
          if !known && !isDriveRoot:
              db.InsertSingleAsync(folder entry); added++
          (a, r) = await SyncDirectoryAsync(dir, token); added += a; removed += r
          if !isDriveRoot:
              db.UpdateFolderModifiedAsync(path, dir.LastWriteTime)   <- so the NEXT pass skips it

      if !root.Recursive -> continue
      foreach sub in dir.EnumerateDirectories():
          if IndexPlanner.IsSkippable(sub) -> continue
          if planner.IsExcluded(sub)       -> continue
          stack.Push(sub)
      (per-directory try/catch swallowed)
```

A drive root has no folder row of its own, so its files are always checked.

```
SyncDirectoryAsync(dir, token)        -- compare the DIRECT children against the index
  try: files = dir.EnumerateFiles().ToList();  subDirs = dir.EnumerateDirectories().ToList()
  catch: return (0, 0)          <<< access denied / disappeared — NEVER treat this as
                                    "everything was deleted"
  indexedNames = await db.GetChildNamesAsync(dir.FullName, token)
  onDisk = HashSet(OrdinalIgnoreCase)

  foreach file:
      skip excluded extension
      onDisk.Add(file.Name)
      if indexedNames.Contains(file.Name) -> continue
      db.InsertSingleAsync(file entry); added++

  foreach subDir:
      skip excluded
      onDisk.Add(subDir.Name)              <- the folder ROW is inserted when the walk reaches it

  foreach staleName in indexedNames:
      if onDisk.Contains(staleName) -> continue
      db.DeleteByPathAsync(Path.Combine(dir.FullName, staleName)); removed++
```

The `catch → return (0, 0)` on enumeration is a safety property, not an optimisation: an
unreadable directory must never be interpreted as an empty one.

`RunCatchUpForRootAsync(rootPath, token)` is an `internal` test seam used by
[IndexCatchUpTests](../../AnythingSearch.Tests/IndexCatchUpTests.cs); production code calls
`RunCatchUpAsync`.

**Note the catch-up pass does not mirror into the in-memory overlay.** It writes only to SQLite;
the snapshot picks its changes up on the next rebuild.

---

## 10. Mirroring into the in-memory index

```
FileWatcherService                       MemorySearchService
   NotifyAdded(entry)   ------------->   Mutate(entry.Path, entry)
   NotifyRemoved(path)  ------------->   Mutate(path, null)
   NotifyUpdated(entry) ------------->   Mutate(entry.Path, entry)
                                            |
                                            v
                              _pending : ConcurrentDictionary<string, FileEntry?>
                              (scanned by every search — see 03-File-Search.md §8)
```

`_memoryIndex` is null until `MainForm` calls `AttachMemoryIndex`; every call site uses `?.`.
`Mutate` returns immediately if the service is disposed or has no snapshot yet.

This mirroring is why a file created a second ago is findable without waiting for the next
snapshot rebuild — and why the delta has a hard overflow limit (`DeltaOverflowLimit = 60,000`):
every search walks it.

---

## 11. Shutdown

```
StopWatching()                                  [FileWatcherService.cs:154]
  lock (_lock)
    if !_isRunning -> return
    _isRunning = false
    _processTimer.Change(Infinite, Infinite)
    foreach watcher: EnableRaisingEvents = false; unsubscribe all 5 events; Dispose()
    _watchers.Clear(); _pendingChanges.Clear(); _pendingCount = 0; _drainScheduled = 0
    StatusChanged("File system monitoring stopped")

StopAsync(timeout)                              [FileWatcherService.cs:280]
  _stopping = true            <- ProcessChangesAsync refuses new batches from here on
  StopWatching()
  deadline = UtcNow + timeout
  while (_processing != 0):
      if UtcNow >= deadline:
          Logger.Log("File watcher shutdown timed out with a change batch still applying.")
          return false
      await Task.Delay(25)
  return true

Dispose()
  _stopping = true; StopWatching(); _processTimer?.Dispose()
```

`StopWatching` alone only stops **new** work: it kills the timer and the watchers, but a batch
already inside `ProcessChangesAsync` keeps writing on a thread-pool thread. The caller then
disposed the database underneath it, which turned closing the app mid-sync into an
`ObjectDisposedException` on the shared connection and its gate. Await `StopAsync` before
disposing anything the watcher writes to.

The wait is **polled** rather than awaited on a stored `Task`: a batch is started from two places
(the interval timer and the high-water-mark drain), and `_processing` is the one flag both go
through, so it is the only thing that cannot miss a batch.

On timeout it returns anyway — the batch is wrapped in a transaction that either committed or did
not, so abandoning it costs at most that batch, which the next launch's catch-up pass picks up.
Hanging on exit would be the worse outcome.

Ordering in `MainForm.ShutdownServicesAsync` ([MainForm.Events.cs:68](../../AnythingSearch/Forms/MainForm.Events.cs)): **watcher first**
(it is the one still writing single entries), then `SearchManager.ShutdownAsync`, both sharing a
single 10-second budget.

---

## 12. Performance considerations and bottlenecks

| Area | Note |
| --- | --- |
| Callback thread | `QueueChange` + `ShouldIgnore` must not allocate and must not take a heavy lock — a slow callback means kernel-buffer overflow and a lost subtree. |
| `InternalBufferSize` | 64 KB per watcher (8× the default), one watcher per fixed drive. |
| `ConcurrentDictionary.Count` | Never used on the event path; `_pendingCount` is maintained with `Interlocked`. Same reasoning as `MemorySearchService._pendingTotal`. |
| Batch transaction | One transaction per batch, not per statement. |
| Walk budget | 4000 entries per batch, sized so the walk stays a fraction of the 3-second interval on a normal disk. Overflow is deferred, capped at 20,000 directories. |
| `ExistsAsync` guard | The difference between a directory `Changed` event costing one round trip and costing a full subtree re-walk. |
| `DeleteByPathAsync` | Its subtree sweep is guarded by an equality lookup on `Folders(Path)`, because most deletions are files. See [04-Database-And-Index-Writing.md](04-Database-And-Index-Writing.md) §7. |
| Overlay growth | Every mirrored change enters the delta every search scans. `DeltaRebuildThreshold = 20,000` and `DeltaOverflowLimit = 60,000` bound it; rebuilds driven by churn respect a 15-second cooldown. |
| Catch-up cost | `GetFolderModifiedMapAsync` materialises one entry per indexed folder up front. The timestamp test is what keeps the per-directory work near zero for an unchanged tree; only changed directories pay a `GetChildNamesAsync` round trip. |
| Catch-up scheduling | Skipped entirely while a build is running — the pipeline is already reading the same disk. |
| Renamed folders | `UpdatePathAsync` moves the folder's own row; descendant folder-path rows are reconciled by the catch-up pass, not by the rename handler. |

---

## 13. Interaction summary

```
   Windows kernel
        |  FileSystemWatcher events (one watcher per fixed drive)
        v
   QueueChange  --(dedupe by path, newest wins)-->  _pendingChanges
        |                                                |
        |  >= 5000 -> Task.Run drain                     |  every 3 s -> timer
        v                                                v
                       ProcessChangesAsync  (Interlocked claim)
                                |
                       ResetWalkBudget(4000)
                                |
                       TakeBatch(debounce 500 ms / age 10 s, sorted D<C<R<M)
                                |
                       BeginIncrementalTransaction
                         DrainDeferredDirectories   (backlog first)
                         HandleDeleted / HandleCreated / HandleRenamed / HandleModified
                            |                          |
                            |                          +-> IndexNewDirectoryAsync -> WalkAsync
                            v                                                          (budgeted)
                       FileDatabase  <-------------------- catch-up pass (startup only)
                            |                              (timestamp-driven reconciliation)
                            v
                       CommitIncrementalTransaction
                            |
                            +--> MemorySearchService._pending  (overlay, scanned by every search)
```
