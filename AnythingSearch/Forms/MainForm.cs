using AnythingSearch.Database;
using AnythingSearch.Services;
using AnythingSearch.Models;
using System.Collections.Concurrent;

namespace AnythingSearch.Forms;

/// <summary>
/// Main application form - split into partial classes for maintainability
/// DPI-aware design for Microsoft Store compliance (150% scaling support)
/// 
/// Search Strategy:
/// 1. Indexing runs in phases in the background: the Downloads folder, then each
///    non-OS drive, then the OS drive. Each phase becomes searchable as soon as it finishes.
/// 2. Search is disabled - with a clear status - only while nothing has been published yet.
/// 3. Once the index is loaded into RAM, searches are answered from memory; SQLite covers the
///    window before the snapshot lands.
/// </summary>
public partial class MainForm : Form
{
    #region Services & Dependencies

    private readonly FileDatabase _database;
    private readonly SettingsManager _settingsManager;
    private readonly SearchManager _searchManager;
    private readonly FileWatcherService _fileWatcher;
    private readonly RecentSearchService _recentSearchService;
    private CancellationTokenSource? _searchCts;

    #endregion

    #region Icon Cache

    private readonly ConcurrentDictionary<string, Image> _iconCache = new();
    private readonly Image _folderIcon;
    private readonly Image _fileIcon;

    #endregion

    #region UI Controls

    private Panel pnlHeader = null!;
    private Panel pnlSearchContainer = null!;
    private TextBox txtSearch = null!;
    private Button btnClearSearch = null!;
    private DataGridView dgvResults = null!;
    private Button btnIndex = null!;
    private Button btnSettings = null!;
    private CheckBox chkAutoWatch = null!;
    private Label lblSearchInfo = null!;
    private Label lblTotalFiles = null!;
    private Label lblWatchStatus = null!;
    private ProgressBar progressBar = null!;
    private ContextMenuStrip contextMenu = null!;
    private MenuStrip menuStrip = null!;

    // Recent searches panel
    private Panel pnlRecentSearches = null!;
    private Label lblRecentTitle = null!;
    private FlowLayoutPanel flpRecentSearches = null!;
    private LinkLabel lnkClearRecent = null!;

    // System Tray
    private NotifyIcon _notifyIcon = null!;
    private ContextMenuStrip _trayContextMenu = null!;
    private bool _isExiting = false;
    private bool _minimizeToTray = true;
    private bool _showBalloonOnMinimize = true;

    #endregion

    #region Constructor

    public MainForm()
    {
        // CRITICAL: Enable DPI awareness BEFORE any controls are created
        // This ensures proper scaling at 150% on high-resolution displays
        this.AutoScaleMode = AutoScaleMode.Dpi;
        this.AutoScaleDimensions = new SizeF(96F, 96F);

        _database = new FileDatabase();
        _settingsManager = new SettingsManager();
        _searchManager = new SearchManager(_database, _settingsManager);
        _fileWatcher = new FileWatcherService(_database, _settingsManager);
        // Changes the watcher writes to SQLite are mirrored into the in-memory index, so a file
        // created moments ago is searchable without waiting for the next snapshot rebuild.
        _fileWatcher.AttachMemoryIndex(_searchManager.MemoryIndex);
        _recentSearchService = new RecentSearchService();

        _minimizeToTray = _settingsManager.Settings.MinimizeToTray;

        _folderIcon = GetStockIcon(StockIconId.Folder);
        _fileIcon = GetStockIcon(StockIconId.DocumentNotAssociated);

        InitializeComponent();
        InitializeSystemTray();
        InitializeAsync();      
    }

    #endregion

    #region Initialization

    private async void InitializeAsync()
    {
        // Subscribe to search manager events
        _searchManager.StatusChanged += OnSearchManagerStatus;
        _searchManager.SearchSourceChanged += OnSearchSourceChanged;
        _searchManager.ProgressChanged += OnIndexingProgress;
        _searchManager.IndexingCompleted += OnIndexingCompleted;
        _searchManager.ScopePublished += OnScopePublished;
        _fileWatcher.StatusChanged += OnWatcherStatus;
        _searchManager.CatchUpStatusChanged += OnWatcherStatus;

        // Show initial status
        lblSearchInfo.Text = "Initializing...";
        lblWatchStatus.Text = "Starting up...";

        try
        {
            // Opens the index, publishes whatever is already stored, and resumes any missing
            // phases in the background. Returns as soon as that decision is made.
            await _searchManager.InitializeAsync();

            // Update UI based on current state
            UpdateSearchSourceUI();
            ApplySearchLock();
            await UpdateTotalCountAsync();

            LoadRecentSearches();

            // Start file watcher if database is ready
            if (_searchManager.IsDatabaseReady && chkAutoWatch.Checked)
            {
                StartFileWatcher();
            }

            // Pick up everything that changed while the app was closed - the watcher above only
            // reports changes from now on. Runs in the background, never blocks the UI. Skipped
            // while indexing is still running: the pipeline is already reading the same disk.
            if (_searchManager.IsDatabaseReady && !_searchManager.IsIndexing)
            {
                _ = _searchManager.RunCatchUpAsync().ContinueWith(
                    _ => SafeInvoke(() => _ = UpdateTotalCountAsync()),
                    TaskScheduler.Default);
            }

            // Update tray status
            UpdateTrayStatus(_searchManager.GetStatusMessage());

            await StartupService.ExecuteStartupTaskAsync();
        }
        catch (Exception ex)
        {
            lblSearchInfo.Text = $"Initialization error: {ex.Message}";
            UpdateTrayStatus("Initialization failed");
        }
    }

    private void UpdateSearchSourceUI()
    {
        SafeInvoke(() =>
        {
            var source = _searchManager.CurrentSource;
            var isIndexing = _searchManager.IsIndexing;

            if (isIndexing)
            {
                btnIndex.Text = "⏳ Indexing...";
                btnIndex.Enabled = false;
                progressBar.Visible = true;
                lblSearchInfo.Text = _searchManager.GetStatusMessage();
                ApplySearchLock();
                return;
            }

            progressBar.Visible = false;
            btnIndex.Enabled = true;
            txtSearch.Enabled = true;

            switch (source)
            {
                case SearchSource.Memory:
                case SearchSource.SQLite:
                    btnIndex.Text = "🔄 Rebuild Index";
                    lblSearchInfo.Text = $"✓ Local database ready ({_searchManager.IndexingStatus.TotalItems:N0} items)";
                    break;

                default:
                    btnIndex.Text = "🔧 Build Index";
                    lblSearchInfo.Text = "No index yet - use Build Index to create one";
                    break;
            }

            // Update watch status
            if (_searchManager.IsDatabaseReady && chkAutoWatch.Checked)
            {
                lblWatchStatus.Text = "Auto-watch: Monitoring file changes";
                lblWatchStatus.ForeColor = AppColors.Success;
            }
        });
    }

    private void OnSearchManagerStatus(string status)
    {
        SafeInvoke(() =>
        {
            if (_searchManager.IsIndexing)
            {
                lblWatchStatus.Text = status;
                lblWatchStatus.ForeColor = AppColors.Warning;
            }
        });
    }

    private void OnSearchSourceChanged(SearchSource source)
    {
        SafeInvoke(() =>
        {
            UpdateSearchSourceUI();

            // Show status in the UI instead of popup notification
            if (source == SearchSource.SQLite || source == SearchSource.Memory)
            {
                // Show success message in watch status area (will be visible for a few seconds)
                lblWatchStatus.Text = $"✓ Local database ready! ({_searchManager.IndexingStatus.TotalItems:N0} items indexed)";
                lblWatchStatus.ForeColor = AppColors.Success;

                // Start file watcher now that database is ready
                if (chkAutoWatch.Checked)
                {
                    StartFileWatcher();
                }

                // Update tray icon tooltip (no balloon popup)
                UpdateTrayStatus($"Ready - {_searchManager.IndexingStatus.TotalItems:N0} items");
            }
        });
    }

    #endregion

    #region Helper Methods

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { try { Invoke(action); } catch { } }
        else if (!IsDisposed && !Disposing) action();
    }

    private string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes; int order = 0;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.##} {sizes[order]}";
    }

    private string TruncatePath(string path, int maxLength) =>
        string.IsNullOrEmpty(path) || path.Length <= maxLength ? path : "..." + path.Substring(path.Length - maxLength + 3);

    private async Task UpdateTotalCountAsync()
    {
        if (IsDisposed || Disposing) return;
        try
        {
            var count = await _searchManager.GetTotalCountAsync();
            if (IsDisposed || Disposing) return;
            SafeInvoke(() =>
            {
                var sourceText = _searchManager.CurrentSource == SearchSource.Memory ? "in memory" : "local";
                lblTotalFiles.Text = $"Total: {count:N0} items ({sourceText})";
            });
        }
        catch { SafeInvoke(() => lblTotalFiles.Text = "Total: 0 items indexed"); }
    }

    #endregion

    #region Dispose

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchManager.StatusChanged -= OnSearchManagerStatus;
            _searchManager.SearchSourceChanged -= OnSearchSourceChanged;
            _searchManager.ProgressChanged -= OnIndexingProgress;
            _searchManager.IndexingCompleted -= OnIndexingCompleted;
            _searchManager.ScopePublished -= OnScopePublished;
            _fileWatcher.StatusChanged -= OnWatcherStatus;
            _searchManager.CatchUpStatusChanged -= OnWatcherStatus;

            if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); }
            _trayContextMenu?.Dispose();
            _searchManager?.Dispose();
            _database?.Dispose();
            _searchCts?.Cancel(); _searchCts?.Dispose();
            _fileWatcher?.Dispose();
            contextMenu?.Dispose();

            foreach (var icon in _iconCache.Values) icon?.Dispose();
            _iconCache.Clear();
            _folderIcon?.Dispose();
            _fileIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    #endregion
}