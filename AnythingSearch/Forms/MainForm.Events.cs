using System.Diagnostics;
using System.Runtime.InteropServices;
using AnythingSearch.Helper;
using AnythingSearch.Models;
using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// Form events and DataGridView handlers for MainForm
/// </summary>
public partial class MainForm
{
    #region Form Events

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        if (_minimizeToTray && this.WindowState == FormWindowState.Minimized)
            MinimizeToTray();
    }

    /// <summary>
    /// Closing happens in two passes, because the background work has to be stopped before the
    /// form is disposed - and the form's Dispose is what disposes the database.
    ///
    /// The first pass cancels the close, hides the window so it looks shut, and awaits the
    /// indexing pipeline and the watcher's in-flight batch. The second pass, entered from the
    /// Close() below, does the actual teardown. It is done this way rather than by blocking here
    /// because the pipeline reports progress through SafeInvoke, i.e. Invoke on this thread: a
    /// synchronous wait would deadlock against the very work it was waiting for.
    /// </summary>
    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isExiting && _minimizeToTray)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        if (!_shutdownComplete)
        {
            e.Cancel = true;

            if (_shutdownStarted) return;   // already draining; ignore repeat close requests
            _shutdownStarted = true;

            await ShutdownServicesAsync();

            _shutdownComplete = true;
            Close();                        // re-enter, this time it goes through
            return;
        }

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _trayContextMenu?.Dispose();
    }

    /// <summary>
    /// Stop everything that touches the database, in dependency order, and wait for it. Runs on
    /// the UI thread but never blocks it.
    /// </summary>
    private async Task ShutdownServicesAsync()
    {
        // Detached first: a form that is on its way out has no business receiving progress
        // callbacks, and this removes the Invoke traffic that would otherwise keep arriving
        // while the pipeline unwinds.
        DetachServiceEvents();

        // Look closed straight away. Draining can take a moment and a window that ignores the
        // close button reads as a hang.
        try
        {
            if (_notifyIcon != null) _notifyIcon.Visible = false;
            Hide();
        }
        catch { /* cosmetic only */ }

        _searchCts?.Cancel();

        try
        {
            // One budget shared between the two waits, not one each - they run in sequence, so
            // separate timeouts would add up and a close could sit there for twice as long.
            var budget = Stopwatch.StartNew();

            TimeSpan Remaining()
            {
                var left = ShutdownTimeout - budget.Elapsed;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }

            // Watcher first: it is the one still writing single entries, and stopping it leaves
            // the pipeline as the only writer to wait for.
            if (_fileWatcher != null)
                await _fileWatcher.StopAsync(Remaining());

            if (_searchManager != null)
                await _searchManager.ShutdownAsync(Remaining());
        }
        catch (Exception ex)
        {
            // Never let shutdown trouble stop the app from closing.
            Logger.Log($"Error while shutting down background services: {ex.Message}");
        }
    }

    private void BtnSettings_Click(object? sender, EventArgs e)
    {
        var settingsForm = new SettingsForm();
        settingsForm.ShowDialog(this);

        // Settings may have changed Minimize-to-Tray; apply it without requiring a restart
        _minimizeToTray = SettingsService.Current.MinimizeToTray;
    }

    #endregion

    #region DataGridView Events

    private void DgvResults_DoubleClick(object? sender, EventArgs e) => ContextMenu_Open(sender, e);

    private void DgvResults_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
        {
            dgvResults.ClearSelection();
            dgvResults.Rows[e.RowIndex].Selected = true;
        }
    }

    private void DgvResults_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        // Path column - gray text like Everything
        if (dgvResults.Columns[e.ColumnIndex].Name == "Path" && e.Value != null)
        {
            e.CellStyle.ForeColor = Color.FromArgb(100, 100, 100);
            e.CellStyle.Font = new Font("Segoe UI", 9F);
        }
        // Name column - black text
        else if (dgvResults.Columns[e.ColumnIndex].Name == "Name" && e.Value != null)
        {
            e.CellStyle.Font = new Font("Segoe UI", 9F);
        }
        // Size column - right aligned gray
        else if (dgvResults.Columns[e.ColumnIndex].Name == "Size" && e.Value != null)
        {
            e.CellStyle.ForeColor = Color.FromArgb(80, 80, 80);
            e.CellStyle.Font = new Font("Segoe UI", 9F);
        }
        // Date column
        else if (dgvResults.Columns[e.ColumnIndex].Name == "Modified" && e.Value != null)
        {
            e.CellStyle.ForeColor = Color.FromArgb(80, 80, 80);
            e.CellStyle.Font = new Font("Segoe UI", 9F);
        }
    }

    //private void DgvResults_KeyDown(object? sender, KeyEventArgs e)
    //{
    //    if (e.KeyCode == Keys.Enter)
    //    {
    //        e.SuppressKeyPress = true;
    //        ContextMenu_Open(sender, e);
    //    }
    //    else if (e.KeyCode == Keys.Escape)
    //    {
    //        e.SuppressKeyPress = true;
    //        txtSearch.Focus();
    //    }
    //}

    #endregion

    #region Icon Handling

    private Image GetCachedIcon(string path, bool isFolder)
    {
        if (isFolder) return _folderIcon;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return _fileIcon;

        if (_iconCache.TryGetValue(ext, out var cachedIcon))
            return cachedIcon;

        try
        {
            if (File.Exists(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    // Create 16x16 icon for compact display
                    var smallIcon = new Icon(icon, 16, 16);
                    var bitmap = smallIcon.ToBitmap();
                    _iconCache[ext] = bitmap;
                    icon.Dispose();
                    return bitmap;
                }
            }
        }
        catch { }

        _iconCache[ext] = _fileIcon;
        return _fileIcon;
    }

    /// <summary>
    /// Resolve the file-type icons for a result set on a thread-pool thread.
    /// Icon.ExtractAssociatedIcon does disk I/O, so doing it while filling the grid
    /// stalled the UI thread (and therefore typing) on the first hit of each extension.
    /// </summary>
    private Task PrewarmIconCacheAsync(List<FileEntry> items, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (item.IsFolder) continue;

                var ext = Path.GetExtension(item.Path).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext) || _iconCache.ContainsKey(ext)) continue;

                GetCachedIcon(item.Path, item.IsFolder);
            }
        }, cancellationToken);
    }

    private Image GetStockIcon(StockIconId id)
    {
        try
        {
            var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf(typeof(SHSTOCKICONINFO)) };
            // SHGSI_ICON = 0x100, SHGSI_SMALLICON = 0x1 (16x16 icon)
            SHGetStockIconInfo((uint)id, 0x000000101, ref info);
            var icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
            DestroyIcon(info.hIcon);
            // Ensure 16x16 size
            var smallIcon = new Icon(icon, 16, 16);
            var bitmap = smallIcon.ToBitmap();
            icon.Dispose();
            return bitmap;
        }
        catch { return SystemIcons.Application.ToBitmap(); }
    }

    #endregion

    #region P/Invoke for Stock Icons

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysIconIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    #endregion
}

/// <summary>
/// Stock icon identifiers
/// </summary>
public enum StockIconId : uint
{
    DocumentNotAssociated = 0,
    Folder = 3,
}