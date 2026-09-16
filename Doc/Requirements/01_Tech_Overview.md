# 01. Project Tech Overview

## What it is
**Anything Search** is a Windows desktop file-search utility (an "Everything"-style tool) distributed via the Microsoft Store. It indexes all fixed drives and lets the user search file/folder names instantly as they type.

- **Product**: Anything Search
- **Developer**: Zero Byte Software Solutions
- **Distribution**: Microsoft Store (Windows 10/11)
- **Website**: https://zerobytebd.com/anything-search
- **Support**: shahedbddev@gmail.com

## Tech stack

| Layer | Technology |
|---|---|
| UI Framework | Windows Forms (WinForms), .NET 8 (`net8.0-windows`) |
| Language | C# 12, nullable reference types enabled |
| Local database | SQLite via `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.bundle_e_sqlite3` |
| OS search integration | Windows Search Index via OLE DB (`System.Data.OleDb`, `Search.CollatorDSO` provider) |
| Hardware/device info | `System.Management` (WMI) |
| Packaging | MSIX via a separate `WinAppPakaging` (Windows Application Packaging Project, `.wapproj`) |
| Concurrency | `System.Threading.Channels` (producer/consumer), `Parallel.ForEach`, `Interlocked` counters |
| Persistence (app state) | JSON files in `%LocalAppData%\AnythingSearch\` (settings, recent searches, indexing status) + legacy `.NET user settings` (`App.config` / `Settings.settings`) |

## Solution layout

`AnythingSearch.sln` contains two projects:

1. **AnythingSearch** (`AnythingSearch\AnythingSearch.csproj`) — the actual WinForms application (`WinExe`).
2. **WinAppPakaging** (`WinAppPakaging\WinAppPakaging.wapproj`) — the MSIX packaging project used to produce the Store submission package (`Package.appxmanifest`, store association, bundle artifacts). It has no source code of its own; it packages the output of `AnythingSearch`.

## Key project settings (`AnythingSearch.csproj`)
- `TargetFramework`: `net8.0-windows`, `UseWindowsForms=true`
- `ApplicationHighDpiMode` / `FormsHighDpiMode`: `PerMonitorV2` — required for Microsoft Store certification at 150% DPI scaling
- Multi-platform build: `AnyCPU;x64;ARM64`
- `WindowsPackageType=None` (packaging is delegated to the separate `.wapproj`)
- Current version: `1.0.1.0` (csproj) — note: `Database/CommonData.cs` separately hardcodes `ApplicationVersion = "1.0.3.0"` (see improvement plan, drift risk)

## NuGet dependencies
- `Microsoft.Data.Sqlite.Core` — SQLite ADO.NET driver (core, no bundled native lib)
- `SQLitePCLRaw.bundle_e_sqlite3` — the native SQLite engine bundle paired with the driver above
- `System.Data.OleDb` — used to query the Windows Search Index (`Search.CollatorDSO` OLE DB provider)
- `System.Management` — WMI queries for hardware info (processor name, memory) in `DeviceInfoCollector`

## How search is powered (dual-engine strategy)
The app never blocks the user waiting for its own index to build. It always has *some* search source available:

1. **Windows Search Index** (via OLE DB `SystemIndex`) — used immediately on startup if the Windows Search service is available. Fast to query, content-search capable, but only covers locations Windows itself indexes.
2. **Local SQLite database** — built in the background by scanning all fixed drives in parallel. Once ready, the app switches to it because it is faster and covers the whole disk (not just Windows-indexed folders).
3. **`SearchManager`** arbitrates between the two sources transparently and falls back to Windows Search if the SQLite path throws.

## Persistence locations
All app data lives under `%LocalAppData%\AnythingSearch\`:
- `AnythingSearch.db` — the SQLite file index (Folders + Files tables)
- `settings.json` — `AppSettings` (excluded folders/extensions, tray/startup preferences)
- `recent_searches.json` — last 10 searches with result counts and timestamps
- `database_status.json` — persisted `DatabaseStatus` (indexing state, counts, timestamps) so state survives app restarts

## Distinctive engineering choices
- **Normalized SQLite schema**: a `Folders` table (path stored once) + a `Files` table referencing `FolderId`, instead of storing the full path per file — claimed ~60–70% space savings.
- **Bulk-load PRAGMAs**: `journal_mode=WAL`, `synchronous=OFF`, large `cache_size`/`mmap_size`, `locking_mode=EXCLUSIVE` during indexing; indexes are created *after* the bulk insert, then the DB is `VACUUM`ed and switched back to `synchronous=NORMAL` / `locking_mode=NORMAL`.
- **Channel-based indexing pipeline**: a bounded `Channel<FileEntry>` (capacity 200,000) with `Parallel.ForEach` producers (one per root/sub directory, using all CPU cores) and a single consumer that batch-inserts into SQLite.
- **Live file-system sync**: `FileSystemWatcher` per fixed drive, with a debounce/batch timer (`FileWatcherService`) so the SQLite index stays current after the initial build.
- **DPI-first WinForms UI**: every control's size/position is computed from `DeviceDpi / 96f` rather than relying solely on `AutoScaleMode`, specifically to satisfy Microsoft Store's 150%-scaling certification requirement.
- **System tray / background-app behavior**: closing the window minimizes to tray by default (`FormClosing` cancels close), consistent with a "search utility that's always running" UX.
