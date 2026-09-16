# 03. Code Flow — How the Project Works

## 1. Startup sequence

```
Program.Main()
 ├─ CommonHelper.PriorProcess() → if another instance is already running, show a MessageBox and exit
 ├─ Application.SetHighDpiMode(PerMonitorV2) + EnableVisualStyles + default font
 └─ Application.Run(new MainForm())
      │
      MainForm constructor
       ├─ new FileDatabase()            (no I/O yet, just paths)
       ├─ new SettingsManager()         (loads settings.json synchronously)
       ├─ new SearchManager(db, settings)
       │     └─ new WindowsSearchService()
       │     └─ new BackgroundIndexingService(db, settings)
       │           └─ DatabaseStatus.Load()   (loads database_status.json synchronously)
       ├─ new FileWatcherService(db, settings)
       ├─ new RecentSearchService()     (loads recent_searches.json synchronously)
       ├─ GetStockIcon() ×2 (folder/file icons via Shell32 P/Invoke)
       ├─ InitializeComponent()         → builds the entire UI tree (Forms/MainForm.Layout.cs)
       ├─ InitializeSystemTray()        → NotifyIcon + tray menu
       └─ InitializeAsync()  (fire-and-forget async void)
             ├─ subscribe to SearchManager/FileWatcher events
             ├─ await _searchManager.InitializeAsync()
             │     ├─ check Windows Search availability (OLE DB connection test)
             │     └─ await BackgroundIndexingService.InitializeAsync()
             │           ├─ FileDatabase.InitializeAsync() → open SQLite connection, create tables, set PRAGMAs
             │           ├─ if DatabaseStatus.IsReady and row count > 0 → fire DatabaseReady event (done, SQLite ready instantly)
             │           └─ else → fire-and-forget StartBackgroundIndexAsync() (see §2) and return immediately
             ├─ UpdateSearchSourceUI() / UpdateTotalCountAsync() → reflect current state in the header/status labels
             ├─ LoadRecentSearches()  → populate the "recent searches" panel shown when the search box is empty
             ├─ if database already ready and Auto-Watch checked → StartFileWatcher()
             └─ StartupService.ExecuteStartupTaskAsync()
                   → on first install only: opens marketing links, POSTs anonymous device info to a remote API (see Doc 04, privacy note)
```

Because `InitializeAsync` is `async void` fired from the constructor, the form **displays immediately** — indexing and Windows-Search availability checks happen in the background while the window is already responsive.

## 2. Background indexing pipeline (`BackgroundIndexingService`)

```
StartBackgroundIndexAsync()
 ├─ status → Indexing, reset counters, create bounded Channel<FileEntry> (capacity 200,000)
 ├─ FileDatabase.ClearAsync() + BeginBatchAsync()   (drop/recreate tables, open a transaction, prepare INSERT statements)
 ├─ spawn 1 consumer Task  → ConsumerAsync(): reads from the channel in batches of 20,000, calls FileDatabase.InsertAsync() per row
 └─ spawn producer Task
       ├─ CollectRootDirectories()
       │     - always scans Downloads first (priority)
       │     - all fixed drives; large top-level folders are split into their own subfolders so Parallel.ForEach
       │       has many independent units of work instead of one giant recursive walk per drive
       ├─ Parallel.ForEach(directories, ScanDirectoryFast)   (one iteration per directory tree, uses all CPU cores)
       │     - stack-based (non-recursive) walk per tree
       │     - writes a FileEntry for every folder and file into the channel (blocks/backs off if the channel is full)
       │     - skips excluded folders (AppSettings.ExcludedFolders) and excluded extensions (AppSettings.ExcludedExtensions)
       │     - every 50,000 files: ReportProgress() → IndexProgress event → DatabaseStatus.UpdateProgress() (persists every 10,000 items)
       ├─ channel.Writer.Complete() once all directories are scanned
       ├─ await consumer task completion
       ├─ FileDatabase.CommitBatchAsync() → flush remaining buffered inserts, COMMIT the transaction
       ├─ FileDatabase.FinalizeIndexingAsync() → CREATE INDEX (name/folder/ext), WAL checkpoint, PRAGMA back to NORMAL, ANALYZE, VACUUM
       ├─ DatabaseStatus.MarkCompleted(files, folders) → persisted, State = Ready
       └─ fire IndexingCompleted + DatabaseReady events
```

`SearchManager` listens for `DatabaseReady` and flips `_useSqlite = true`, then fires `SearchSourceChanged(SQLite)`. `MainForm` reacts by updating the status labels and — if Auto-Watch is checked — starting `FileWatcherService`.

## 3. Search flow (as the user types)

```
User types in txtSearch
 → TxtSearch_TextChanged
      ├─ ignore placeholder text / empty text → show recent-searches panel instead
      ├─ require ≥2 characters
      ├─ cancel any in-flight search (CancellationTokenSource)
      ├─ await Task.Delay(400ms, token)   ← debounce: only the last keystroke within 400ms actually searches
      └─ PerformSearchAsync(searchText, token)
            ├─ (results, source) = await SearchManager.SearchAsync(searchText, maxResults: 1000, token)   ← always executed on a thread-pool thread
            │     ├─ if SQLite ready:
            │     │     multi-word query (contains a space) → FileDatabase.SearchAdvancedAsync (AND over LIKE per term)
            │     │     single word            → FileDatabase.SearchAsync (relevance-ranked LIKE: exact > starts-with > contains > path-contains)
            │     │     on exception → falls back to WindowsSearchService.SearchAsync if available
            │     └─ else → WindowsSearchService.SearchAsync (OLE DB query against SystemIndex), or SQLite as last resort
            ├─ pre-warm the per-extension icon cache off the UI thread (Icon.ExtractAssociatedIcon does disk I/O)
            ├─ populate dgvResults in a single Rows.AddRange
            └─ save to RecentSearchService if query ≥3 chars and looks like a "new" search (debounced/coalesced so
               typing "rep" → "report" doesn't create two separate recent-search entries)
```

> **Threading rule**: neither Microsoft.Data.Sqlite nor the Windows Search OLE DB provider implement real async I/O — their
> `*Async` methods run synchronously on the calling thread. `SearchManager.Query.cs` therefore pushes every query onto a
> thread-pool thread (serialized by a semaphore because the SQLite connection is shared) so the message pump — and typing —
> is never blocked.

Pressing **Enter** force-saves the current query to recent searches; **Escape** minimizes to tray; **↓** moves focus into the results grid.

## 4. Manual "Build/Rebuild Index" flow

`BtnIndex_Click` (`MainForm.Indexing.cs`) branches on current state:
- **Currently indexing** → offers to cancel (`SearchManager.CancelIndexing()` → `BackgroundIndexingService.CancelIndexing()` → cancels the token, marks status Failed with "cancelled by user").
- **Database already ready** → confirms, stops the file watcher, calls `SearchManager.RebuildIndexAsync()` (temporarily flips back to Windows Search, resets `DatabaseStatus`, restarts the pipeline in §2).
- **Never indexed / failed** → confirms and starts indexing for the first time.

Progress is streamed back to the UI via `SearchManager.ProgressChanged`/`IndexingCompleted` events → `MainForm.OnIndexingProgress` / `OnIndexingCompleted`, which update the status labels, tray tooltip, and (every 10,000 items) the total-count label.

## 5. Live file-system sync (`FileWatcherService`)

Runs only once the SQLite database is ready and "Auto-Watch" is checked:
- One `FileSystemWatcher` per fixed drive (`IncludeSubdirectories = true`, 64KB internal buffer).
- Every raw OS event (`Created`/`Deleted`/`Renamed`/`Changed`) is deduplicated into a `ConcurrentDictionary<path, FileSystemChange>`. The **newest event wins** — an older `Deleted` must never outrank a newer `Created`, because editors and git save files atomically (write temp → delete target → rename), which would otherwise drop live files from the index.
- A `Timer` fires every 3 seconds and flushes each queued path once it has been quiet for 500ms **or** has been waiting for 10s (`MaxChangeAgeMs`). Without the age limit, a path that keeps changing (a log file, an in-progress download) refreshes its own debounce forever, fills the 10,000-entry queue, and every other change is dropped. When the queue does reach capacity it is flushed immediately instead of discarding the incoming change.
- Handlers re-check the path on disk before writing (`FileWatcherService.SyncHandlers.cs`): a `Deleted` event for a path that exists again is treated as a create, and a rename whose old path was never indexed indexes the new path instead. OS events are a hint that something changed, never the authority on what the file system now contains.
- Each change is applied via a targeted `FileDatabase` call (`InsertSingleAsync` / `DeleteByPathAsync` / `UpdatePathAsync` / `UpdateFileAsync`), batched into one transaction, so the index stays live without a full rescan.
- A watcher that errors out is automatically restarted after a 5-second delay.

### 5b. Catch-up pass (`BackgroundIndexingService.CatchUp.cs`)

The watcher only sees changes while the app is running, so anything created or deleted while it was
closed would stay invisible until a full rebuild. On startup, once the database is ready, `MainForm`
kicks off `SearchManager.RunCatchUpAsync()` on a thread-pool thread:

1. Load every indexed folder with the `LastWriteTime` it had when indexed (`GetFolderModifiedMapAsync`).
2. Walk the same scan roots as the indexer. A directory's `LastWriteTime` changes whenever an entry is
   added or removed inside it, so only directories whose timestamp no longer matches are re-read.
3. For those, diff the directory's children against `GetChildNamesAsync` — insert what is new, delete
   what is gone (never when enumeration failed), then refresh the folder's stored timestamp.

## 6. Result interaction (context menu / double-click)

`MainForm.ContextMenu.cs` provides: **Open** (double-click or menu — `Process.Start` the file, or `explorer.exe` for folders), **Open File Location** (`explorer /select,`), **Copy Full Path / Copy Name** (clipboard), **Properties** (native Shell "properties" dialog via `ShellExecuteEx` P/Invoke), **Delete** (confirms, then `File.Delete`/`Directory.Delete`, then re-runs the current search to refresh the grid). None of these operations touch the SQLite index directly — the next file-watcher cycle (or next search) reconciles it.

## 7. Settings flow

`SettingsForm` (opened via header button, `Ctrl+S`, or tray menu) edits `AppSettings` in-memory, then on **Save**: persists via `SettingsManager.Save()` and separately writes/removes an `HKCU\...\Run` registry value for "Start with Windows". Note: the "Start with Windows" and "Minimize to tray" checkboxes are currently **not added to the form's `Controls` collection** (commented out in `SettingsForm.cs`), so despite being fully wired up in code, the user cannot see or toggle them today — see the Improvement Plan (Doc 04).

## 8. Shutdown / minimize-to-tray

- `MainForm_FormClosing`: if not explicitly exiting (tray "Exit" or menu "Exit"), the close is cancelled and the window is hidden instead (`MinimizeToTray()`), with a one-time balloon tip.
- A genuine exit (`_isExiting = true`) unsubscribes all events, stops the file watcher, disposes the `NotifyIcon`, `SearchManager` (which disposes `BackgroundIndexingService`), and `FileDatabase` (closes the SQLite connection).

## High-level data/control diagram

```
                       ┌───────────────────────┐
                       │        MainForm        │  (WinForms UI, partial classes)
                       └───────────┬────────────┘
                                   │ events / calls
                                   ▼
                       ┌───────────────────────┐
                       │      SearchManager      │  chooses SQLite vs Windows Search
                       └───┬───────────────┬─────┘
              ┌────────────┘               └─────────────┐
              ▼                                           ▼
 ┌─────────────────────────┐                 ┌─────────────────────────┐
 │ BackgroundIndexingService│  scans disk,   │   WindowsSearchService   │  OLE DB → OS index
 │  (Channel + Parallel)    │  fills SQLite  │                          │
 └────────────┬─────────────┘                 └─────────────────────────┘
              ▼
     ┌─────────────────┐        ┌─────────────────────┐
     │   FileDatabase   │◄──────┤  FileWatcherService  │  keeps index live
     │  (SQLite: Files, │        └─────────────────────┘
     │   Folders)       │
     └─────────────────┘
```
