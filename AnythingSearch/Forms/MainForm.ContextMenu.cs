using System.Runtime.InteropServices;

namespace AnythingSearch.Forms;

/// <summary>
/// Context menu and file operations for MainForm
/// </summary>
public partial class MainForm
{
    #region Context Menu Initialization

    private void InitializeContextMenu()
    {
        contextMenu = new ContextMenuStrip { Font = new Font("Segoe UI", 9.5F) };
        contextMenu.Renderer = new ModernToolStripRenderer();

        contextMenu.Items.Add(new ToolStripMenuItem("Open", null, ContextMenu_Open) { Font = new Font("Segoe UI", 9.5F, FontStyle.Bold) });
        contextMenu.Items.Add(new ToolStripMenuItem("Open File Location", null, ContextMenu_OpenFolder));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Copy Full Path", null, ContextMenu_CopyPath));
        contextMenu.Items.Add(new ToolStripMenuItem("Copy Name", null, ContextMenu_CopyName));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Properties", null, ContextMenu_Properties));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Delete", null, ContextMenu_Delete) { ForeColor = Color.FromArgb(220, 50, 50) });
    }

    #endregion

    #region Context Menu Event Handlers

    private void ContextMenu_Open(object? sender, EventArgs e)
    {
        var path = GetSelectedPath();
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                if (File.Exists(path))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
                else if (Directory.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            }
            catch (Exception ex) { MessageBox.Show($"Cannot open: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    private void ContextMenu_OpenFolder(object? sender, EventArgs e)
    {
        var path = GetSelectedPath();
        if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private void ContextMenu_CopyPath(object? sender, EventArgs e)
    {
        var path = GetSelectedPath();
        if (!string.IsNullOrEmpty(path))
        {
            Clipboard.SetText(path);
            lblSearchInfo.Text = "✓ Path copied to clipboard";
        }
    }

    private void ContextMenu_CopyName(object? sender, EventArgs e)
    {
        var name = GetSelectedName();
        if (!string.IsNullOrEmpty(name))
        {
            Clipboard.SetText(name);
            lblSearchInfo.Text = "✓ Name copied to clipboard";
        }
    }

    private void ContextMenu_Properties(object? sender, EventArgs e)
    {
        var path = GetSelectedPath();
        if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
            ShowFileProperties(path);
    }

    private void ContextMenu_Delete(object? sender, EventArgs e)
    {
        var path = GetSelectedPath();
        if (string.IsNullOrEmpty(path)) return;

        if (MessageBox.Show($"Are you sure you want to delete:\n{path}", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); lblSearchInfo.Text = "✓ File deleted"; }
                else if (Directory.Exists(path)) { Directory.Delete(path, true); lblSearchInfo.Text = "✓ Folder deleted"; }
                TxtSearch_TextChanged(null, EventArgs.Empty);
            }
            catch (Exception ex) { MessageBox.Show($"Cannot delete: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    #endregion

    #region Helper Methods

    private string? GetSelectedPath() => dgvResults.SelectedRows.Count > 0 ? dgvResults.SelectedRows[0].Cells["Path"].Value?.ToString() : null;
    private string? GetSelectedName() => dgvResults.SelectedRows.Count > 0 ? dgvResults.SelectedRows[0].Cells["Name"].Value?.ToString() : null;

    private void ShowFileProperties(string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO)),
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,
            fMask = 12
        };
        ShellExecuteEx(ref info);
    }

    #endregion

    #region P/Invoke for File Properties

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    #endregion
}
