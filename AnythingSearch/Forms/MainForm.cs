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
/// 1. On startup, uses Windows Search for immediate results (if available)
/// 2. Background indexing builds SQLite database in parallel
/// 3. Once SQLite is ready, automatically switches to it for faster results
/// 4. Falls back to Windows Search if SQLite fails
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
        _recentSearchService = new RecentSearchService();

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
        _fileWatcher.StatusChanged += OnWatcherStatus;

        // Show initial status
        lblSearchInfo.Text = "Initializing...";
        lblWatchStatus.Text = "Starting up...";

        try
        {
            // Initialize the search manager
            // This will:
            // 1. Check Windows Search availability
            // 2. Check if SQLite database is ready
            // 3. Start background indexing if needed
            await _searchManager.InitializeAsync();

            // Update UI based on current state
            UpdateSearchSourceUI();
            await UpdateTotalCountAsync();

            LoadRecentSearches();

            // Start file watcher if database is ready
            if (_searchManager.IsDatabaseReady && chkAutoWatch.Checked)
            {
                StartFileWatcher();
            }

            // Update tray status
            UpdateTrayStatus(_searchManager.GetStatusMessage());
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
                lblSearchInfo.Text = _searchManager.IndexingStatus.GetStatusMessage();
            }
            else
            {
                progressBar.Visible = false;
                btnIndex.Enabled = true;

                switch (source)
                {
                    case SearchSource.SQLite:
                        btnIndex.Text = "🔄 Rebuild Index";
                        lblSearchInfo.Text = $"✓ Local database ready ({_searchManager.IndexingStatus.TotalItems:N0} items)";
                        break;

                    case SearchSource.WindowsSearch:
                        btnIndex.Text = "🔍 Build Index";
                        lblSearchInfo.Text = "Using Windows Search (building local index...)";
                        break;

                    default:
                        btnIndex.Text = "🔧 Build Index";
                        lblSearchInfo.Text = "Search initializing...";
                        break;
                }
            }

            // Update watch status
            if (_searchManager.IsDatabaseReady)
            {
                if (chkAutoWatch.Checked)
                {
                    lblWatchStatus.Text = "Auto-watch: Monitoring file changes";
                    lblWatchStatus.ForeColor = AppColors.Success;
                }
            }
            else if (_searchManager.IsWindowsSearchAvailable)
            {
                lblWatchStatus.Text = "Windows Search: Active";
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
            if (source == SearchSource.SQLite)
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
                var source = _searchManager.CurrentSource;
                var sourceText = source == SearchSource.SQLite ? "local" : "Windows Search";
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
            _fileWatcher.StatusChanged -= OnWatcherStatus;

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