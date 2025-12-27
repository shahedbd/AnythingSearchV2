using System.Runtime.InteropServices;

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

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isExiting && _minimizeToTray)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        // Cleanup
        _fileWatcher?.StopWatching();
        _searchCts?.Cancel();

        if (_searchManager != null)
        {
            _searchManager.StatusChanged -= OnSearchManagerStatus;
            _searchManager.SearchSourceChanged -= OnSearchSourceChanged;
            _searchManager.ProgressChanged -= OnIndexingProgress;
            _searchManager.IndexingCompleted -= OnIndexingCompleted;
        }

        if (_fileWatcher != null)
            _fileWatcher.StatusChanged -= OnWatcherStatus;

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _trayContextMenu?.Dispose();
    }

    private void BtnSettings_Click(object? sender, EventArgs e)
    {
        var settingsForm = new SettingsForm(_settingsManager);
        settingsForm.ShowDialog(this);
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