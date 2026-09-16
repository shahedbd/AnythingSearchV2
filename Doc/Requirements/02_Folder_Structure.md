# 02. Folder Structure

```
AnythingSearchV2/
├── AnythingSearch.sln                  Solution (2 projects)
│
├── AnythingSearch/                     Main WinForms application project
│   ├── Program.cs                      Entry point (Main): single-instance check, DPI mode, Application.Run(MainForm)
│   ├── AnythingSearch.csproj           net8.0-windows, WinForms, SQLite/OleDb/Management refs
│   ├── App.config                      Legacy .NET user-settings config (IsNewInstallations, AppInstalledDate)
│   ├── app.manifest                    Win32 app manifest (DPI awareness declarations)
│   │
│   ├── Forms/                          All UI (Windows Forms), split via C# `partial class`
│   │   ├── MainForm.cs                 Fields, constructor, InitializeAsync, dispose — the "core" partial
│   │   ├── MainForm.Layout.cs          Programmatic UI construction (no .Designer for the real layout — see note below)
│   │   ├── MainForm.Events.cs          Form-level events, DataGridView events, icon extraction (P/Invoke stock icons)
│   │   ├── MainForm.Search.cs          Search textbox handling, debounce, results-grid population
│   │   ├── MainForm.RecentSearches.cs  Recent-searches panel rendering
│   │   ├── MainForm.Indexing.cs        Build/Rebuild Index button, indexing progress/completion handlers, file-watcher start/stop
│   │   ├── MainForm.SystemTray.cs      NotifyIcon, tray context menu, minimize/restore
│   │   ├── MainForm.ContextMenu.cs     Right-click grid menu: Open, Open Location, Copy, Properties, Delete
│   │   ├── MainForm.Theme.cs           AppColors palette, modern ToolStrip renderer, small UI helpers
│   │   ├── MainForm.Designer.cs        Auto-generated designer stub (mostly unused — see note)
│   │   ├── MainForm.resx               Designer resource file
│   │   ├── SettingsForm.cs             Settings dialog: excluded folders list, start-with-Windows / tray checkboxes
│   │   ├── SettingsForm.Designer.cs / .resx
│   │   ├── AboutForm.cs                About dialog (product info, links, version)
│   │   └── AboutForm.Designer.cs / .resx
│   │
│   ├── Services/                       Application/business logic (no UI dependencies)
│   │   ├── SearchManager.cs            Orchestrates WindowsSearchService vs SQLite; the class MainForm actually talks to
│   │   ├── SearchManager.Query.cs      Query execution — runs every query on a thread-pool thread so the UI never blocks
│   │   ├── WindowsSearchService.cs     Queries the OS's own Windows Search Index via OLE DB
│   │   ├── Backgroundindexingservice.cs  Full-disk parallel scanner that builds the SQLite index (the one actually used)
│   │   ├── IndexingService.cs          ⚠️ Near-duplicate of BackgroundIndexingService — appears unused (see Doc 04)
│   │   ├── SearchService.cs            ⚠️ Thin FileDatabase wrapper — appears unused, superseded by SearchManager (see Doc 04)
│   │   ├── FileWatcherService.cs       FileSystemWatcher per drive + debounced batch sync into SQLite
│   │   ├── BackgroundIndexingService.CatchUp.cs  Startup reconcile — re-reads only directories whose timestamp changed
│   │   ├── SettingsManager.cs          Load/save AppSettings to settings.json
│   │   ├── RecentSearchService.cs      Load/save last 10 searches to recent_searches.json
│   │   ├── StartupService.cs           First-run tasks: open product/profile links, submit device telemetry
│   │   └── DeviceInfoCollector.cs      Collects machine/OS/hardware info and posts it to a remote API (opt-in-by-install)
│   │
│   ├── Database/                       Data access
│   │   ├── FileDatabase.cs             SQLite schema, bulk insert/consumer path, search queries
│   │   ├── FileDatabase.Incremental.cs Single-entry writes (watcher/catch-up) + per-batch transactions
│   │   ├── FileDatabase.Queries.cs     Folder-timestamp / child-name queries used by the catch-up pass
│   │   └── CommonData.cs               Static constants: app name/version, URLs, API keys/endpoints
│   │
│   ├── Models/                         Plain data classes
│   │   ├── FileEntry.cs                One file/folder row (Name, Path, Extension, Size, Modified, IsFolder)
│   │   ├── AppSettings.cs              Persisted user preferences (excluded folders/extensions, tray/startup flags)
│   │   ├── DatabaseStatus.cs           Persisted indexing state machine (NotStarted/Indexing/Ready/Failed/Updating)
│   │   ├── IndexProgress.cs            Transient progress snapshot pushed via events during indexing
│   │   └── UserDeviceInfoNsmPlus.cs    Telemetry payload shape sent by DeviceInfoCollector
│   │
│   ├── Helper/
│   │   └── CommonHelper.cs             Icon loading, internet-check P/Invoke, single-instance detection, process launching
│   │
│   ├── Properties/
│   │   ├── Settings.settings / .Designer.cs   Legacy strongly-typed user settings (IsNewInstallations, AppInstalledDate)
│   │
│   ├── Resources/                      App icons (favicon.ico, favicon_io/*)
│   └── ProjectNotes/                   Free-form dev notes (not part of the shipped product)
│       ├── Notes.txt                   Publish commands, data paths, misc scratch notes
│       └── z_ai_note.txt               Instructions for AI-assisted doc generation (this very task)
│
├── WinAppPakaging/                     MSIX packaging project (`.wapproj`) — no source code
│   ├── Package.appxmanifest            Store identity, capabilities, visual assets manifest
│   ├── Package.StoreAssociation.xml    Links the project to its Partner Center Store listing
│   ├── Images/                         Store tile/logo assets
│   └── BundleArtifacts/                Build output (generated MSIX bundles)
│
└── Doc/                                Product & release documentation (non-code)
    ├── Product_Info.txt                Marketing copy: description, features, "what's new"
    ├── MS_Store_Release_Role.txt        Release process notes
    ├── *.png                           Store screenshot/logo assets at required Store sizes
    ├── Sh/                             Additional screenshots
    └── Requirements/                   ← this documentation set
```

## Naming/organization notes worth knowing
- **`partial class MainForm`** is spread across 8 files in `Forms/`. This is a deliberate "split by concern" pattern (layout, events, search, indexing, tray, context menu, theme) rather than one giant form file. `MainForm.Designer.cs` exists (VS scaffolding) but the *real* UI construction happens in `MainForm.Layout.cs`'s `InitializeComponent()` — the layout is built entirely in code (DPI-scaled `Point`/`Size` math), not via the WinForms visual designer.
- **`Services/Backgroundindexingservice.cs`** — filename casing is inconsistent with the class name `BackgroundIndexingService` and with the rest of the codebase's PascalCase file naming (everything else matches its class name exactly).
- **Two "search sources", one search entry point**: UI code (`MainForm.Search.cs`) never talks to `WindowsSearchService` or `FileDatabase` directly — it always goes through `SearchManager`, which is the single seam between UI and data.
- **`Database/CommonData.cs`** mixes unrelated concerns: app branding strings, a *different* product's Store link (`NetSpeedMeterProMicrosoftStore`), a PayPal donation link, and a live API secret key/URL — this file is shared/copied from another product in the same developer's portfolio (see Doc 04).
