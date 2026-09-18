# 04 — Database Creation & Index Writing

The SQLite store: schema, connection management, the three write paths, and the maintenance that
keeps the file small.

> **Related documents**
> [01-Indexing.md](01-Indexing.md) ·
> [02-Rebuild-Indexing.md](02-Rebuild-Indexing.md) ·
> [03-File-Search.md](03-File-Search.md) ·
> [05-Auto-Watch.md](05-Auto-Watch.md)

---

## 1. Purpose

`FileDatabase` is the durable store behind everything else. It is optimised for **both speed and
size**:

* **Size** — normalized folder paths (stored once, referenced by id), compact integer ids,
  WAL checkpointing, `VACUUM`.
* **Speed** — prepared statements with parameter reuse, large batch inserts, aggressive PRAGMAs
  during a bulk build, and search indexes created after bulk insert (with one exception, below).

It is a `partial class` split across five files:

| File | Responsibility |
| --- | --- |
| [FileDatabase.cs](../../AnythingSearch/Database/FileDatabase.cs) | Schema, connection, gate, PRAGMAs, bulk write path. |
| [FileDatabase.Incremental.cs](../../AnythingSearch/Database/FileDatabase.Incremental.cs) | Single-entry insert/delete/rename/update + the incremental transaction. |
| [FileDatabase.Queries.cs](../../AnythingSearch/Database/FileDatabase.Queries.cs) | Read queries used by the catch-up pass. |
| [FileDatabase.Search.cs](../../AnythingSearch/Database/FileDatabase.Search.cs) | The SQLite fallback search — see [03-File-Search.md](03-File-Search.md). |
| [FileDatabase.Maintenance.cs](../../AnythingSearch/Database/FileDatabase.Maintenance.cs) | Once-per-database schema repair and compaction. |

---

## 2. File location and creation

```csharp
_dbPath = Path.Combine(
    ApplicationDataManager.Instance.ApplicationDataDirectory, "AnythingSearch.db");
```

`ApplicationDataManager`, not a hand-built `%LocalAppData%` path — it creates and validates the
directory and falls back when it is unwritable, which a hard-coded `Path.Combine` cannot do.

The constructor also runs `SQLitePCL.Batteries.Init()` once per process (guarded by a static
`_initialized` flag). A `dbPathOverride` parameter exists so automated tests never touch the real
user database.

Three files exist on disk at runtime: `AnythingSearch.db`, `AnythingSearch.db-wal`,
`AnythingSearch.db-shm`.

---

## 3. Schema

`InitializeAsync` ([FileDatabase.cs:125](../../AnythingSearch/Database/FileDatabase.cs)):

```sql
CREATE TABLE IF NOT EXISTS Folders (
    Id    INTEGER PRIMARY KEY,
    Path  TEXT NOT NULL COLLATE NOCASE
);

CREATE TABLE IF NOT EXISTS Files (
    Id        INTEGER PRIMARY KEY,
    Name      TEXT NOT NULL COLLATE NOCASE,
    FolderId  INTEGER NOT NULL,
    Ext       TEXT COLLATE NOCASE,
    Size      INTEGER,
    Modified  INTEGER,          -- DateTime.Ticks
    IsFolder  INTEGER DEFAULT 0
);
```

```
    Folders                         Files
 +----+-----------------+       +----+--------+----------+-----+------+----------+----------+
 | Id | Path            |<------| Id | Name   | FolderId | Ext | Size | Modified | IsFolder |
 +----+-----------------+       +----+--------+----------+-----+------+----------+----------+
 |  1 | D:\Projects     |       |  1 | app.cs |    1     | cs  | 4096 | <ticks>  |    0     |
 |  2 | D:\Projects\src |       |  2 | src    |    1     | ""  |    0 | <ticks>  |    1     |
 +----+-----------------+       +----+--------+----------+-----+------+----------+----------+

 Full path of a row = Folders.Path + '\' + Files.Name
```

Notes:

* **Folders is the normalization table**, not a directory tree. A folder appears in `Files` too
  (with `IsFolder = 1`, under its *parent's* folder id) so it can be searched by name; it appears
  in `Folders` so its children can reference it. This saves ~60–70 % of the space full paths
  would take.
* A **drive root has no row in `Files`** — it has no parent folder to be listed in. Both
  `ScanUnit` and the catch-up pass special-case `dir.Parent == null`.
* `Modified` is `DateTime.Ticks` as an integer. For folders it is the directory's
  `LastWriteTime`, which is what the catch-up pass compares against
  (see [05-Auto-Watch.md](05-Auto-Watch.md)).
* `COLLATE NOCASE` on `Path`, `Name` and `Ext` — a file system cannot hold two entries with the
  same name in the same folder, case-insensitively.

### Indexes

| Index | Created by | Used for |
| --- | --- | --- |
| `idx_files_folder_name_unique` — `UNIQUE(FolderId, Name)` | `PrepareForBulkIndexingAsync` (**before** the inserts) and `EnsureUniqueEntriesAsync` | Deduplication on every insert path; also answers every `Name` lookup the watcher and catch-up pass make. |
| `idx_folders_path` — `Folders(Path)` | `PrepareForBulkIndexingAsync` | The path → folder-id lookup behind each of those. |

**The unique index is created before the bulk inserts, not after.** Phased indexing writes into a
database that already holds earlier phases, and a resumed run re-walks the chunk that was in
flight when it stopped, so `INSERT OR IGNORE` needs the index in place to drop the repeats. That
costs some insert throughput and buys resumability without duplicates.

**Two indexes were deliberately removed** — `idx_files_name` and `idx_files_ext`. Measured on a
1,320,717-entry database by dropping each object and re-vacuuming: 37.0 MiB and 14.6 MiB, together
**51.6 MiB of a 191.3 MiB file (27 %)**. Neither could ever be chosen:

* `Files(Name)` cannot serve the search — the query is `Name LIKE '%term%'`, and a leading
  wildcard rules a B-tree out; the `ESCAPE '\'` clause disables SQLite's LIKE-to-range
  optimisation on top of that. The plan is `SCAN Files`. Every other query matching on `Name`
  also constrains `FolderId`, which the unique index already covers.
* `Files(Ext)` was never read at all — its own `CREATE` was the only mention of `Ext` in any
  statement the class issues.

`DropUnusedIndexesAsync` removes them from databases built by earlier versions.

---

## 4. Connection management

**One shared `SqliteConnection`**, used by the search box, the file watcher, the catch-up pass and
the index build — all on different threads.

```csharp
new SqliteConnection($"Data Source={_dbPath};Pooling=True;Cache=Shared")
```

SQLite will not let the same connection write to a table while one of its own readers is still
open (*"database table is locked"*), and `Microsoft.Data.Sqlite` connections are not thread-safe.
Every operation is therefore serialized through a gate:

```csharp
private readonly SemaphoreSlim _gate = new(1, 1);

private async Task<IDisposable> LockAsync(CancellationToken ct = default)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    await _gate.WaitAsync(ct).ConfigureAwait(false);
    if (_disposed) { _gate.Release(); throw new ObjectDisposedException(nameof(FileDatabase)); }
    return new GateReleaser(_gate);
}
```

The disposal flag is re-checked **after** the wait: the gate may have been held by the operation
still running when shutdown began.

```
  search box ---+
  file watcher -+---> LockAsync() ---> [ _gate ] ---> shared SqliteConnection
  catch-up -----+                                            |
  bulk build ---+                                            v
                                                    AnythingSearch.db
                                                            ^
  MemoryIndexBuilder --- its OWN read-only connection -------+
                         (Mode=ReadOnly; Pooling=False)
```

The in-memory snapshot builder is the one component that bypasses the gate entirely, because it
opens a separate read-only connection — WAL mode lets it read while the writer works. See
[03-File-Search.md](03-File-Search.md).

### Disposal

```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;                 // set FIRST, so late callers are refused
    _insertFileCommand?.Dispose(); _insertFolderCommand?.Dispose(); _selectFolderCommand?.Dispose();
    _connection?.Dispose();
    // _gate is deliberately NOT disposed
}
```

`_gate` is not disposed on purpose: `SemaphoreSlim` only holds an OS resource once
`AvailableWaitHandle` has been read (nothing does), so disposing it buys nothing — and it would
throw `ObjectDisposedException` into any thread still parked in `WaitAsync`, which is exactly the
shutdown crash the `_disposed` guard exists to prevent.

---

## 5. PRAGMA profiles

Two profiles, switched explicitly.

### Applied once at open — `InitializeAsync`

```
PRAGMA journal_mode = WAL
PRAGMA temp_store   = MEMORY
PRAGMA page_size    = 4096
PRAGMA auto_vacuum  = NONE
```

### Runtime — `ApplyRuntimeSettingsAsync`

```
PRAGMA synchronous   = NORMAL      -- watcher writes must survive a crash
PRAGMA cache_size    = -16000      -- 16 MB
PRAGMA mmap_size     = 67108864    -- 64 MB
PRAGMA locking_mode  = NORMAL
PRAGMA busy_timeout  = 5000        -- wait briefly instead of failing when the snapshot is reading
```

These used to be the *bulk* settings, left in place for the whole session — a 256 MB page cache
and a 512 MB mapping for the life of the process. That made sense when every keystroke queried
SQLite; searches now come from the in-memory index, so the page cache was hundreds of MB doing
nothing.

### Bulk build — `ApplyBulkBuildSettingsAsync`

```
PRAGMA synchronous   = OFF         -- a bulk build can simply be redone
PRAGMA cache_size    = -64000      -- 64 MB
PRAGMA mmap_size     = 268435456   -- 256 MB
PRAGMA locking_mode  = NORMAL      -- NOT exclusive, see below
```

`locking_mode` stays `NORMAL` unlike the old rebuild-everything build: indexing now publishes
each phase as it finishes, so the in-memory index opens its read-only connection to the same file
while later phases are still writing, and `EXCLUSIVE` would lock it out. The page cache is 64 MB
rather than 256 MB for the same reason — the process is answering searches at the same time, and
that memory is better spent on the search index.

`FinalizeIndexingAsync` restores the runtime profile at the end of a build.

---

## 6. Write path A — bulk indexing

Used by `BackgroundIndexingService` (see [01-Indexing.md](01-Indexing.md)).

```
PrepareForBulkIndexingAsync()          once per pipeline run
    ApplyBulkBuildSettingsAsync()
    CREATE UNIQUE INDEX IF NOT EXISTS idx_files_folder_name_unique ON Files(FolderId, Name)
    CREATE INDEX IF NOT EXISTS idx_folders_path ON Folders(Path)

  ... then, per chunk ...

BeginBatchAsync()                      once per chunk
    if already in a transaction -> return
    BEGIN TRANSACTION
    prepare _insertFolderCommand  "INSERT INTO Folders (Path) VALUES (@path); SELECT last_insert_rowid();"
    prepare _selectFolderCommand  "SELECT Id FROM Folders WHERE Path = @path COLLATE NOCASE"
    prepare _insertFileCommand    "INSERT OR IGNORE INTO Files (...) VALUES (@n,@f,@e,@s,@m,@i)"

InsertAsync(entry)                     called once per walked entry
    _pendingInserts.Add(entry)                         (ConcurrentBag)
    if _pendingInserts.Count >= 10000 -> FlushPendingInsertsAsync()

FlushPendingInsertsAsync()
    if Interlocked.CompareExchange(_flushing, 1, 0) != 0 -> return    (a flush is already running)
    using var dbLock = await LockAsync()               <- ONE gate acquisition per batch, not per row
    drain the bag into a List
    foreach entry:
        folderId = GetOrCreateFolderId(Path.GetDirectoryName(entry.Path))
        set parameters; _insertFileCommand.ExecuteNonQuery()
    finally: _flushing = 0

CommitBatchAsync()                     once per chunk
    await FlushPendingInsertsAsync()   <- FIRST, on its own: it takes the same non-reentrant gate
    using var dbLock = await LockAsync()
    COMMIT (if in a transaction)
    dispose + null the three prepared commands

FinalizeScopeAsync()                   once per scope (and per segment)
    PRAGMA wal_checkpoint(TRUNCATE)    <- cheap; fold the WAL back so the snapshot build reads one file

FinalizeIndexingAsync()                once, at the very end
    PRAGMA wal_checkpoint(TRUNCATE)
    ApplyRuntimeSettingsAsync()
    ANALYZE
    VACUUM
    PRAGMA wal_checkpoint(TRUNCATE)    <- see below
```

`_flushing` is an `int` flag rather than a `Monitor` lock, because the flush now awaits the
connection gate and a `Monitor` lock cannot be released on a different thread than the one that
took it — which is exactly what happens after an `await`.

Bulk writes used to bypass the gate entirely, because a full rebuild owned the database. Phased
indexing publishes as it goes, so a search or a count can now reach the shared connection while a
chunk is being written.

### The trailing checkpoint in `FinalizeIndexingAsync`

In WAL mode a `VACUUM` rebuilds the database **into** the write-ahead log, so without a final
checkpoint the main file never shrinks and the log is left holding a second copy of everything.
After a 1.3 M-entry build that was a 191 MiB database beside a 192 MiB log — double the footprint,
and the first snapshot then had to read both.

### Folder id resolution

```
GetOrCreateFolderId(folderPath)           (synchronous; called from inside the flush)
  |
  +-- _folderCache.TryGetValue -> hit? return id      (ConcurrentDictionary, OrdinalIgnoreCase)
  |
  +-- lock (_folderLock)
        double-check the cache
        _selectFolderCommand -> found? cache + return  <- the folder may be from an earlier phase
                                                          or an earlier run; only a genuine miss
                                                          costs a query
        _insertFolderCommand -> new id, cache + return
```

Folder ids are assigned by **SQLite**, not by a private counter: the `Folders` table survives
across phases and across restarts, so a counter starting at 1 would collide with rows written
earlier.

---

## 7. Write path B — incremental (single entry)

[FileDatabase.Incremental.cs](../../AnythingSearch/Database/FileDatabase.Incremental.cs). Used by the file watcher and the catch-up pass. Each
method takes the gate itself and builds its own command; none requires `BeginBatchAsync`.

| Method | SQL shape | Notes |
| --- | --- | --- |
| `InsertSingleAsync(entry)` | `INSERT OR IGNORE INTO Files …` | Folder id via the **async** `GetOrCreateFolderIdAsync`. `OR IGNORE` + the unique index reject a repeated watcher event. |
| `DeleteByPathAsync(path)` | see below | Two statements plus a guard lookup. |
| `UpdatePathAsync(old, new)` | `UPDATE OR REPLACE Files SET Name, FolderId WHERE …` | Returns rows updated; **0 means the old path was never indexed**, so the caller indexes the new path instead. `OR REPLACE` because a destination name already indexed is stale — the rename just overwrote it on disk — and it keeps the rename from failing against the unique index. |
| `UpdateFileAsync(entry)` | `UPDATE Files SET Ext, Size, Modified WHERE Name = … AND FolderId IN (…)` | Metadata refresh only. |
| `ExistsAsync(path)` | `SELECT COUNT(*) …` | Seeks the unique index. |

### `DeleteByPathAsync` — the subtle one

```
1. DELETE FROM Files
   WHERE Name = @name AND FolderId IN (SELECT Id FROM Folders WHERE Path = @folder COLLATE NOCASE)
        -- removes the entry itself

2. SELECT Id FROM Folders WHERE Path = @path COLLATE NOCASE
        -- was the deleted path itself an indexed FOLDER?
        -- if not (the overwhelming majority of deletions are files) -> RETURN
        -- this equality lookup seeks idx_folders_path and is what keeps step 3 off the hot path

3. DELETE FROM Files
   WHERE FolderId = @id
      OR FolderId IN (SELECT Id FROM Folders
                      WHERE Path >= @from AND Path < @to
                        AND Path LIKE @pathPrefix ESCAPE '\')
        -- @from = path + "\"      @to = path + "]"      ( ']' is the next char after '\' )
        -- @pathPrefix = EscapeLike(path) + "\%"
```

Three things are load-bearing in step 3:

* `FolderId = @id` covers the files sitting **directly** inside the deleted folder. The prefix
  cannot match them — their folder path *is* the deleted path, with no trailing separator — so
  they used to survive the delete as unreachable rows that searches still returned.
* The prefix **must** be escaped: `_` is a single-character wildcard in `LIKE` and real paths are
  full of underscores (`C:\Program Files\…`, `G:\src\MS_Store_App\…`), so an unescaped prefix also
  deleted rows belonging to unrelated sibling folders.
* But an `ESCAPE` clause disables SQLite's LIKE-to-range optimisation outright, which made this a
  full scan of every folder ever indexed — hundreds of thousands of rows — **per deleted entry**,
  while holding the connection gate the search box queues on. The explicit `>= / <` range restores
  the index seek; `LIKE` stays on as the exact filter over what it returns.

`EscapeLike(value)` = `value.Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")`, always
paired with `ESCAPE '\'`.

### Incremental transaction

```
BeginIncrementalTransactionAsync()   -> BEGIN TRANSACTION   (guarded by _inIncrementalTransaction)
   ... a batch of InsertSingle / DeleteByPath / UpdatePath / UpdateFile ...
CommitIncrementalTransactionAsync()  -> COMMIT
```

Wraps a watcher batch in one transaction instead of SQLite's default one-transaction-per-statement
autocommit. The watcher always commits in a `finally`.

Note this is a **separate flag** from the bulk `_inTransaction`; the watcher is stopped before a
bulk build starts, so the two are never nested in practice.

---

## 8. Read queries for the catch-up pass

[FileDatabase.Queries.cs](../../AnythingSearch/Database/FileDatabase.Queries.cs):

```sql
-- GetFolderModifiedMapAsync: every indexed folder + the LastWriteTime it had when indexed
SELECT fo.Path || '\' || f.Name AS FullPath, f.Modified
FROM Files f INNER JOIN Folders fo ON f.FolderId = fo.Id
WHERE f.IsFolder = 1;

-- GetChildNamesAsync: names of everything indexed directly inside a folder
SELECT f.Name FROM Files f INNER JOIN Folders fo ON f.FolderId = fo.Id
WHERE fo.Path = @folder COLLATE NOCASE;

-- UpdateFolderModifiedAsync: refresh a folder row's stored timestamp
UPDATE Files SET Modified = @m
WHERE Name = @n AND IsFolder = 1
  AND FolderId IN (SELECT Id FROM Folders WHERE Path = @folder COLLATE NOCASE);
```

`GetFolderModifiedMapAsync` materialises the whole folder set into a
`Dictionary<string, long>` — see the cost note in section 10.

---

## 9. Maintenance

[FileDatabase.Maintenance.cs](../../AnythingSearch/Database/FileDatabase.Maintenance.cs). All of it runs from
`SearchManager.RunStartupMaintenanceAsync`, in the background, after the first snapshot has
settled and only when indexing is **not** running. All of it reports through
`MaintenanceStatusChanged`, which `SearchManager` relays as `StatusChanged`.

### `EnsureUniqueEntriesAsync` — deduplication

```
if idx_files_folder_name_unique already exists -> return 0     (a single catalogue lookup)

try CREATE UNIQUE INDEX ...
catch SqliteException:                                          <- existing duplicates
    MaintenanceStatusChanged("Removing duplicate entries from the index...")
    CREATE INDEX IF NOT EXISTS idx_files_folder_name ON Files(FolderId, Name)
        -- turns the grouping below into an ordered index walk instead of a sort over every row:
        -- seconds instead of minutes on a multi-million-row index
    removed = SELECT COUNT(*) - COUNT(DISTINCT FolderId || '\' || Name) FROM Files
    DELETE FROM Files WHERE rowid NOT IN (SELECT MIN(rowid) FROM Files GROUP BY FolderId, Name)
    DROP INDEX idx_files_folder_name
    CREATE UNIQUE INDEX idx_files_folder_name_unique ON Files(FolderId, Name)

DROP INDEX IF EXISTS idx_files_folder      -- strict prefix of the new index
ANALYZE
```

A file system cannot hold two entries with the same name in the same folder, but the schema never
said so, so every insert path was free to add another copy. On the machine this was developed
against: **1.4 million redundant rows — 42 % of the index**. Duplicates cost twice over — every
search scans them, and the user sees the same file several times.

### `PruneOrphanFoldersAsync`

```
removed = SELECT COUNT(*) FROM Folders WHERE NOT EXISTS (SELECT 1 FROM Files WHERE Files.FolderId = Folders.Id)
if removed == 0 -> return 0
DELETE FROM Folders WHERE NOT EXISTS (...)
_folderCache.Clear()          <-- ESSENTIAL
```

Deleting a folder removes its entries from `Files` but leaves the `Folders` row behind, and only
the file watcher ever deletes anything — so on a long-watched machine this is pure accumulation
from build output, temp trees and uninstalled programs.

It is not harmless dead weight: the snapshot loads every folder path into one contiguous blob and
every search scans that blob once per term, so orphans make every keystroke scan further for
results that no longer exist.

The cache clear is mandatory — it maps path → id and has just been invalidated for every pruned
path. Left in place, the next file written under one of those paths would be stored against a
folder id that no longer exists and would come back from a search with the wrong path.

### `DropUnusedIndexesAsync`

Drops `idx_files_name` and `idx_files_ext` if present (27 % of the file, section 3). Returns
`true` if anything was dropped, so the caller knows a compaction is worth running — a `DROP` only
marks pages free *inside* the database.

### `CompactIfFragmentedAsync(wasteThreshold = 0.20, minimumBytesToReclaim = 32 MiB)`

```
pageCount, pageSize, freeList   (three PRAGMAs)
liveBytes     = (pageCount - freeList) * pageSize
mainFileBytes = size of AnythingSearch.db   (WAL deliberately NOT counted - it is working space)
wasted        = max(freeList * pageSize, mainFileBytes - liveBytes)

if wasted < 32 MiB                          -> 0
if wasted / mainFileBytes < 0.20            -> 0

before = FootprintBytes()                   (db + -wal + -shm)
try:
    PRAGMA wal_checkpoint(TRUNCATE)
    VACUUM
    PRAGMA wal_checkpoint(TRUNCATE)         <- the file only shrinks once the rebuild is folded back
catch SqliteException -> return 0           (another connection holds the file; next startup retries)

reclaimed = before - FootprintBytes()
```

Two different kinds of waste, and the clean-up leaves both: free pages sitting inside the
database, and a main file still bigger than the database it now holds. Checking only the first
meant an already-compacted database kept a file twice the size it needed.

---

## 10. Performance considerations and bottlenecks

| Area | Note |
| --- | --- |
| **One writer** | The shared connection has a single writer serialized by `_gate`. `BackgroundIndexingService` runs exactly one consumer per chunk for this reason — extra consumers only contended for the same lock. Measured in isolation the writer absorbs ~276,000 rows/sec, against a whole-pipeline rate of ~21,000 items/sec with two walkers, so it has an order of magnitude of headroom today. |
| **Gate contention** | Every incremental write, every count, every fallback search and every bulk flush queues on `_gate`. This is why `DeleteByPathAsync`'s subtree sweep is guarded by an equality lookup, and why `FileWatcherService` runs on a bounded walk budget (see [05-Auto-Watch.md](05-Auto-Watch.md)). |
| **`InsertAsync`** | Reads `_pendingInserts.Count` on **every** entry. `ConcurrentBag<T>.Count` is not a cached field. |
| **Batch sizing** | `BulkInsertThreshold = 10000` — one gate acquisition per 10,000 rows rather than per row. The `List` is pre-sized to `BulkInsertThreshold + 1000` because the bag can grow between the check and the drain. |
| **Prepared statements** | The three bulk commands are prepared once per chunk in `BeginBatchAsync` and disposed in `CommitBatchAsync`. The incremental methods build a fresh `SqliteCommand` per call — acceptable at watcher volumes, not at bulk volumes. |
| **Folder cache** | Keeps the path → id lookup out of SQLite for repeat hits; a miss costs one `SELECT` before any `INSERT`. |
| **`GetFolderModifiedMapAsync`** | Materialises one dictionary entry per indexed **folder** — on a typical machine hundreds of thousands of strings, held for the whole catch-up pass. |
| **`GetCountAsync`** | `SELECT COUNT(*) FROM Files` is a full scan. `SearchManager.GetTotalCountAsync` prefers `_memorySearch.Count` and only falls back to this inside a `Task.Run`. |
| **`VACUUM`** | Needs the file to itself. Best-effort everywhere: if another connection is busy it simply returns and the next startup tries again. This is also why `MemoryIndexBuilder` uses `Pooling=False` and why maintenance waits on `FirstSnapshotSettled`. |
| **`busy_timeout = 5000`** | Waits briefly instead of failing outright when the snapshot builder is reading the file. |
| **`synchronous`** | `OFF` during a bulk build (redoable), `NORMAL` at runtime (watcher writes must survive a crash). |

---

## 11. Lifecycle summary

```
 App start
   |
   +-- FileDatabase ctor              path resolved, SQLitePCL init
   +-- InitializeAsync()              open, CREATE TABLE IF NOT EXISTS, base PRAGMAs, runtime PRAGMAs
   |
   +-- [indexing needed]
   |     PrepareForBulkIndexingAsync()          bulk PRAGMAs + the two indexes
   |     per chunk: BeginBatchAsync -> InsertAsync* -> CommitBatchAsync
   |     per scope/segment: FinalizeScopeAsync()   wal_checkpoint
   |     at the end: FinalizeIndexingAsync()       checkpoint, runtime PRAGMAs, ANALYZE, VACUUM, checkpoint
   |
   +-- [snapshot settled, not indexing] startup maintenance
   |     EnsureUniqueEntriesAsync -> PruneOrphanFoldersAsync
   |     -> DropUnusedIndexesAsync -> CompactIfFragmentedAsync
   |
   +-- [steady state] watcher batches
   |     BeginIncrementalTransactionAsync -> InsertSingle/Delete/UpdatePath/UpdateFile
   |     -> CommitIncrementalTransactionAsync
   |
   +-- [rebuild] ClearAsync()          DROP + CREATE, drain pending, clear folder cache
   |
   +-- Shutdown
         watcher.StopAsync -> searchManager.ShutdownAsync -> database.Dispose()
         (writers first, then the thing they write to)
```
