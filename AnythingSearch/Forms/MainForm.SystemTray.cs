using AnythingSearch.Helper;
using AnythingSearch.Models;
using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// System Tray functionality for MainForm
/// </summary>
public partial class MainForm
{
    #region System Tray Initialization

    private void InitializeSystemTray()
    {
        _trayContextMenu = new ContextMenuStrip();
        _trayContextMenu.Font = new Font("Segoe UI", 9.5F);
        _trayContextMenu.Renderer = new ModernToolStripRenderer();

        var openItem = new ToolStripMenuItem($"📂  Open {AppConfig.AppName}")
        {
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };
        openItem.Click += TrayMenu_Open_Click;

        var separator1 = new ToolStripSeparator();

        var rebuildItem = new ToolStripMenuItem("🔄  Rebuild Index");
        rebuildItem.Click += (s, e) =>
        {
            ShowFromTray();
            BtnIndex_Click(s, e);
        };

        var settingsItem = new ToolStripMenuItem("⚙️  Settings");
        settingsItem.Click += (s, e) =>
        {
            ShowFromTray();
            BtnSettings_Click(s, e);
        };

        var separator2 = new ToolStripSeparator();

        var statusItem = new ToolStripMenuItem("📊  Status: Initializing...")
        {
            Enabled = false,
            Name = "statusItem"
        };

        var sourceItem = new ToolStripMenuItem("🔍  Source: --")
        {
            Enabled = false,
            Name = "sourceItem"
        };

        var separator3 = new ToolStripSeparator();

        var exitItem = new ToolStripMenuItem("🚪  Exit");
        exitItem.Click += TrayMenu_Exit_Click;

        _trayContextMenu.Items.AddRange(new ToolStripItem[]
        {
            openItem, separator1, rebuildItem, settingsItem,
            separator2, statusItem, sourceItem, separator3, exitItem
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = File.Exists(AppConfig.FaviconPath) ? new Icon(AppConfig.FaviconPath) : null,
            Text = $"{AppConfig.AppName} - Quick File Search",
            Visible = true,
            ContextMenuStrip = _trayContextMenu
        };

        _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
    }

    #endregion

    #region System Tray Event Handlers

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e) => ShowFromTray();

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ShowFromTray();
    }

    private void TrayMenu_Open_Click(object? sender, EventArgs e) => ShowFromTray();

    private void TrayMenu_Exit_Click(object? sender, EventArgs e)
    {
        _isExiting = true;
        Application.Exit();
    }

    #endregion

    #region System Tray Methods

    private void ShowFromTray()
    {
        this.Show();
        this.WindowState = FormWindowState.Normal;
        this.Activate();
        this.BringToFront();
        txtSearch.Focus();
    }

    private void MinimizeToTray()
    {
        this.Hide();
        if (_showBalloonOnMinimize)
        {
            var sourceText = _searchManager.CurrentSource switch
            {
                SearchSource.Memory => "the in-memory index",
                SearchSource.SQLite => "the local database",
                _ => "a partial index - still indexing"
            };

            _notifyIcon.ShowBalloonTip(2000, AppConfig.AppName,
                $"Running in background using {sourceText}.\nDouble-click the tray icon to open.",
                ToolTipIcon.Info);
            _showBalloonOnMinimize = false;
        }

        ReleaseIdleWorkingSet();
    }

    /// <summary>
    /// Ask Windows to page out the working set on the way into the tray.
    ///
    /// This app spends nearly all of its life minimised, holding an in-memory index that is
    /// deliberately large - about 90 MB of packed name and folder blobs for a 1.4 million entry
    /// drive. Almost none of that is touched while the window is hidden: the file watcher writes
    /// to SQLite and appends to a small delta, and nothing scans the blobs until someone types.
    /// Trimming here hands those pages back, which is the difference between a tray icon that
    /// reads as ~100 MB in Task Manager and one that reads as a few megabytes.
    ///
    /// The trade is real and worth stating: the pages are not freed, they are unmapped, so the
    /// first search after the window comes back has to fault them in again. In practice Windows
    /// keeps them on the standby list while there is free memory, so that costs a memory copy
    /// rather than a disk read - but on a machine under memory pressure they will have gone to
    /// the page file, and that first search pays for it. Doing this only on the way into the
    /// tray, rather than on a timer or after every search, keeps the cost to at most once per
    /// hide and never in the middle of someone typing.
    ///
    /// Best effort by design. EmptyWorkingSet is advisory, and a failure here costs nothing worth
    /// reporting or interrupting the hide for.
    /// </summary>
    private static void ReleaseIdleWorkingSet()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
        }
        catch
        {
            // Advisory call - nothing downstream depends on it having worked.
        }
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr processHandle);

    private void UpdateTrayStatus(string status)
    {
        if (_notifyIcon != null)
        {
            var tooltip = $"{AppConfig.AppName} - {status}";
            _notifyIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 60) + "..." : tooltip;

            if (_trayContextMenu.Items["statusItem"] is ToolStripMenuItem statusItem)
                statusItem.Text = $"📊  {status}";

            // Update source indicator
            if (_trayContextMenu.Items["sourceItem"] is ToolStripMenuItem sourceItem)
            {
                var source = _searchManager?.CurrentSource ?? SearchSource.None;
                var sourceText = source switch
                {
                    SearchSource.Memory => "In-memory index",
                    SearchSource.SQLite => "Local Database",
                    _ => "Indexing..."
                };
                sourceItem.Text = $"🔍  Source: {sourceText}";
            }
        }
    }

    private void ShowTrayNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon?.ShowBalloonTip(3000, title, message, icon);
    }

    #endregion
}