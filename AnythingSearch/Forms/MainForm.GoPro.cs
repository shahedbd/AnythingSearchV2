using AnythingSearch.Helper;
using AnythingSearch.Services.Store;

namespace AnythingSearch.Forms;

/// <summary>
/// Go Pro entry points on MainForm: the header button, the Help menu items and the tray item.
/// All of them open GoProForm and all re-render when ProLicenseManager.ProStatusChanged fires.
/// See Doc/Requirements/05_Go_Pro_Design.md §3-§4.
/// </summary>
public partial class MainForm
{
    private Button btnGoPro = null!;
    private ToolStripMenuItem? _goProMenuItem;
    private ToolStripMenuItem? _restoreMenuItem;
    private ToolStripMenuItem? _goProTrayItem;
    private readonly ToolTip _goProToolTip = new();

    #region Setup

    /// <summary>Called once from the constructor, after the header, menu and tray exist.</summary>
    private void InitializeGoPro()
    {
        UpdateGoProUi();

        ProLicenseManager.Instance.ProStatusChanged += OnProStatusChanged;
        FormClosed += (_, _) => ProLicenseManager.Instance.ProStatusChanged -= OnProStatusChanged;

        // The Store purchase dialog needs a real window handle, which only exists once loaded.
        Load += async (_, _) =>
        {
            ProLicenseManager.Instance.Initialize(Handle);
            await ProLicenseManager.Instance.RefreshAsync();
        };
    }

    private Button CreateGoProButton(Point location, Size size)
    {
        var btn = CreateModernButton(string.Empty, location, size);
        btn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btn.Click += (_, _) => ShowGoPro();
        return btn;
    }

    /// <summary>"Go Pro..." and "Restore Purchase" for the Help menu.</summary>
    private ToolStripItem[] CreateGoProMenuItems()
    {
        _goProMenuItem = ColorIcons.MenuItem("Go Pro...", ColorIcons.ProStar, this, (_, _) => ShowGoPro());
        _restoreMenuItem = ColorIcons.MenuItem("Restore Purchase", ColorIcons.Update, this, RestoreMenuItem_Click);
        return new ToolStripItem[] { _goProMenuItem, _restoreMenuItem };
    }

    private ToolStripMenuItem CreateGoProTrayItem()
    {
        _goProTrayItem = ColorIcons.MenuItem("Go Pro...", ColorIcons.ProStar, this, (_, _) => ShowGoPro());
        return _goProTrayItem;
    }

    #endregion

    #region Actions

    /// <summary>Opens the Go Pro / "You're Pro" dialog, restoring the window from the tray first.</summary>
    private void ShowGoPro()
    {
        if (!Visible || WindowState == FormWindowState.Minimized)
            ShowFromTray();

        using var dialog = new GoProForm();
        dialog.ShowDialog(this);
    }

    private async void RestoreMenuItem_Click(object? sender, EventArgs e)
    {
        bool reached = await ProLicenseManager.Instance.RefreshAsync();

        if (ProLicenseManager.Instance.IsPro)
        {
            ShowGoPro();
            return;
        }

        MessageBox.Show(this,
            reached
                ? "No Pro purchase found for this Microsoft account."
                : "Couldn't reach Microsoft Store. Try again later.",
            "Restore Purchase", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnProStatusChanged(object? sender, EventArgs e) => SafeInvoke(UpdateGoProUi);

    #endregion

    #region Render

    /// <summary>Switches every entry point between its Free and Pro state (design §4, §6).</summary>
    private void UpdateGoProUi()
    {
        bool isPro = ProLicenseManager.Instance.IsPro;

        // The star carries the amber; the text keeps the theme's own color for contrast.
        ColorIcons.SetButtonIcon(btnGoPro, ColorIcons.ProStar, isPro ? "PRO" : "Go Pro");
        btnGoPro.FlatAppearance.BorderSize = isPro ? 0 : 1;
        _goProToolTip.SetToolTip(btnGoPro, isPro
            ? $"{AppConfig.AppName} Pro - thank you!"
            : "One-time payment - No ads - Lifetime updates & support");

        string menuText = isPro ? $"{AppConfig.AppName} Pro" : "Go Pro...";
        if (_goProMenuItem != null) _goProMenuItem.Text = menuText;
        if (_goProTrayItem != null) _goProTrayItem.Text = menuText;
        if (_restoreMenuItem != null) _restoreMenuItem.Visible = !isPro;
    }

    #endregion
}
