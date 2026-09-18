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

    /// <summary>The grid's first column, which holds the file-type icon.</summary>
    private const int IconColumnIndex = 0;

    /// <summary>
    /// Bumped every time a new set of rows needs icons. A background fill belonging to an older
    /// generation stops as soon as it notices, so scrolling quickly - or typing another
    /// character - never leaves a stale fill writing into the grid behind the current results.
    /// </summary>
    private int _iconFillGeneration;

    /// <summary>
    /// The icon for one result, from the cache only. This is called once per row while the grid
    /// is being filled, so it must never touch the disk: an unresolved extension gets the
    /// generic file icon now and the real one a moment later, from
    /// <see cref="RefreshVisibleIcons"/>.
    /// </summary>
    private Image GetCachedIcon(string path, bool isFolder)
    {
        if (isFolder) return _folderIcon;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return _fileIcon;

        return _iconCache.TryGetValue(ext, out var cached) ? cached : _fileIcon;
    }

    /// <summary>
    /// Resolve the icons for the extensions visible on screen, then drop them into those rows.
    ///
    /// This used to be a prewarm that the search awaited before it could show anything, and it
    /// resolved every extension in the whole 1,000 row page by calling
    /// Icon.ExtractAssociatedIcon on a real file - one disk open per extension, through the
    /// virus scanner, on paths scattered across every drive. A query whose page happened to be
    /// all folders skipped it entirely and felt instant; the moment the page contained files it
    /// cost seconds, and the grid could not paint until it finished. Measured against this
    /// machine's index: "sha" filled the page with 1,000 folders and resolved nothing, while
    /// "shahed" pulled in 943 files across 33 extensions and the search went from 4 ms to 8 s.
    ///
    /// Three things changed. Icons are resolved from the extension alone and never open a file
    /// (see <see cref="ResolveExtensionIcon"/>), so the cost cannot grow with how busy the disk
    /// is. Only the rows actually on screen are resolved - about thirty, not a thousand. And
    /// none of it blocks the search: the grid paints immediately with the generic icon.
    /// </summary>
    private void RefreshVisibleIcons()
    {
        if (IsDisposed || Disposing) return;
        if (dgvResults == null || !dgvResults.Visible || dgvResults.Rows.Count == 0) return;

        var generation = Interlocked.Increment(ref _iconFillGeneration);

        List<string>? wanted = null;
        foreach (var index in DisplayedRowIndices())
        {
            if (dgvResults.Rows[index].Tag is not FileEntry entry || entry.IsFolder) continue;

            var ext = Path.GetExtension(entry.Path).ToLowerInvariant();
            if (ext.Length == 0 || _iconCache.ContainsKey(ext)) continue;

            wanted ??= new List<string>();
            if (!wanted.Contains(ext)) wanted.Add(ext);
        }

        // Everything on screen already has its icon - the common case once a few pages have been
        // looked at, and what makes this cheap enough to call on every scroll notification.
        if (wanted == null) return;

        _ = Task.Run(() =>
        {
            foreach (var ext in wanted)
            {
                if (Volatile.Read(ref _iconFillGeneration) != generation) return;

                // A failed lookup is cached as the generic icon too, so an extension the shell
                // has nothing for is not asked about again on every scroll.
                _iconCache.TryAdd(ext, ResolveExtensionIcon(ext) ?? _fileIcon);
            }

            SafeInvoke(() =>
            {
                if (Volatile.Read(ref _iconFillGeneration) == generation)
                    ApplyIconsToVisibleRows();
            });
        });
    }

    /// <summary>
    /// The rows currently on screen, which is all the icons anyone can actually see.
    ///
    /// Called immediately after the rows are added, when the grid may not have laid itself out
    /// yet - and an unlaid-out grid reports no first row and no displayed row count. Falling
    /// back to what the control's height can hold means the first screenful still gets its
    /// icons, instead of waiting for the user to scroll before anything appears.
    /// </summary>
    private IEnumerable<int> DisplayedRowIndices()
    {
        if (dgvResults.Rows.Count == 0) yield break;

        int first = Math.Max(0, dgvResults.FirstDisplayedScrollingRowIndex);

        int displayed = dgvResults.DisplayedRowCount(true);
        if (displayed <= 0)
            displayed = dgvResults.ClientSize.Height / Math.Max(1, dgvResults.RowTemplate.Height) + 1;

        int last = Math.Min(first + displayed, dgvResults.Rows.Count);
        for (int i = first; i < last; i++)
            yield return i;
    }

    /// <summary>Put the freshly resolved icons into the rows showing them.</summary>
    private void ApplyIconsToVisibleRows()
    {
        foreach (var index in DisplayedRowIndices())
        {
            var row = dgvResults.Rows[index];
            if (row.Tag is not FileEntry entry) continue;

            var icon = GetCachedIcon(entry.Path, entry.IsFolder);
            if (!ReferenceEquals(row.Cells[IconColumnIndex].Value, icon))
                row.Cells[IconColumnIndex].Value = icon;
        }
    }

    /// <summary>
    /// The shell's icon for a file type, resolved from the extension without touching the file
    /// system. SHGFI_USEFILEATTRIBUTES tells the shell to answer from the registered file type
    /// rather than look the path up, so the "file" named here need not exist - which is the
    /// whole point, because the cache is keyed by extension and the particular file behind any
    /// one result is irrelevant to the icon it gets.
    /// </summary>
    private static Image? ResolveExtensionIcon(string extension)
    {
        try
        {
            var info = new SHFILEINFO();
            var result = SHGetFileInfo(
                "file" + extension,
                FILE_ATTRIBUTE_NORMAL,
                ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                // FromHandle does not take ownership, so the handle is released below either way.
                using var icon = Icon.FromHandle(info.hIcon);
                using var small = new Icon(icon, 16, 16);
                return small.ToBitmap();
            }
            finally { DestroyIcon(info.hIcon); }
        }
        catch { return null; }
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

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

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