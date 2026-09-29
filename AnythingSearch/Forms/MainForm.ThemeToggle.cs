using System.Runtime.InteropServices;
using AnythingSearch.Helper;
using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// Dark/light theme support for MainForm: the header's theme toggle button, and re-coloring
/// every long-lived control when ThemeManager.ThemeChanged fires. Controls read their initial
/// colors from AppColors (which already follows the active theme); ApplyTheme only has to
/// repaint what exists at the moment the theme flips.
/// </summary>
public partial class MainForm
{
    private Button btnTheme = null!;
    private readonly ToolTip _themeToolTip = new();

    #region Setup

    /// <summary>Called once from the constructor, after InitializeComponent.</summary>
    private void InitializeTheme()
    {
        ApplyTheme();
        ThemeManager.Instance.ThemeChanged += OnThemeChanged;
        FormClosed += (_, _) => ThemeManager.Instance.ThemeChanged -= OnThemeChanged;

        // Title bar and scrollbars are native; their handles only exist once the form loads.
        Load += (_, _) => ApplyNativeTheme();
    }

    private Button CreateThemeButton(Point location, Size size)
    {
        var btn = CreateModernButton(string.Empty, location, size);
        btn.Font = new Font(ColorIcons.IconFontName, 12F);
        btn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btn.Click += (_, _) => ThemeManager.Instance.ToggleTheme();
        return btn;
    }

    #endregion

    #region Apply

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        SafeInvoke(() =>
        {
            RemapStatusColor(lblWatchStatus, ThemeManager.Instance.GetTheme(e.WasDarkTheme));
            ApplyTheme();
            ApplyNativeTheme();

            // Recent-search rows are rebuilt rather than recolored; keep the search tracking
            // LoadRecentSearches resets, since the user did not start a new search.
            var saved = _lastSavedSearch;
            LoadRecentSearches();
            _lastSavedSearch = saved;
        });
    }

    private void ApplyTheme()
    {
        SuspendLayout();

        BackColor = AppColors.Background;
        menuStrip.BackColor = AppColors.Surface;

        // Header
        pnlHeader.BackColor = AppColors.Surface;
        pnlSearchContainer.BackColor = AppColors.Surface;
        txtSearch.BackColor = AppColors.Surface;
        txtSearch.ForeColor = txtSearch.Text == "Search files and folders..." ? AppColors.TextMuted : AppColors.TextPrimary;
        btnClearSearch.FlatAppearance.MouseOverBackColor = AppColors.DangerHover;
        chkAutoWatch.ForeColor = AppColors.TextPrimary;
        foreach (var btn in new[] { btnIndex, btnSettings, btnTheme })
            StyleModernButton(btn);
        UpdateThemeButtonIcon();

        // Recent searches
        pnlRecentSearches.BackColor = AppColors.Surface;
        flpRecentSearches.BackColor = AppColors.Surface;
        lblRecentTitle.ForeColor = AppColors.TextPrimary;
        lnkClearRecent.LinkColor = AppColors.Link;
        lnkClearRecent.ActiveLinkColor = AppColors.PrimaryDark;

        ApplyGridTheme();

        // Status bar
        if (lblSearchInfo.Parent is Control statusPanel)
        {
            statusPanel.BackColor = AppColors.StatusBar;
            foreach (var lbl in statusPanel.Controls.OfType<Label>().Where(l => l.Text == "⋱"))
                lbl.ForeColor = AppColors.TextMuted;
        }
        lblSearchInfo.ForeColor = AppColors.TextSecondary;
        lblTotalFiles.ForeColor = AppColors.Primary;

        ResumeLayout();
        Invalidate(true);   // border Paint handlers read AppColors
    }

    private void ApplyGridTheme()
    {
        dgvResults.BackgroundColor = AppColors.Surface;
        dgvResults.GridColor = AppColors.Surface;

        // Assign new style objects rather than mutating the current ones: the DefaultCellStyle
        // getter hands back a throwaway copy whenever one of its core properties (Alignment,
        // WrapMode, ...) is unset - which ours are - so in-place edits were silently lost and
        // every non-alternating row stayed white in dark mode.
        dgvResults.DefaultCellStyle = RowStyle(dgvResults.DefaultCellStyle, AppColors.Surface);
        dgvResults.AlternatingRowsDefaultCellStyle = RowStyle(dgvResults.AlternatingRowsDefaultCellStyle, AppColors.SurfaceAlt);

        dgvResults.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle(dgvResults.ColumnHeadersDefaultCellStyle)
        {
            BackColor = AppColors.GridHeader,
            ForeColor = AppColors.TextPrimary,
            SelectionBackColor = AppColors.GridHeader,
            SelectionForeColor = AppColors.TextPrimary
        };
    }

    private static DataGridViewCellStyle RowStyle(DataGridViewCellStyle current, Color backColor) =>
        new(current)
        {
            BackColor = backColor,
            ForeColor = AppColors.TextPrimary,
            SelectionBackColor = AppColors.SelectionBack,
            SelectionForeColor = Color.White
        };

    /// <summary>Shared styling for header buttons (also used by CreateModernButton).</summary>
    private static void StyleModernButton(Button btn)
    {
        btn.BackColor = AppColors.Surface;
        btn.ForeColor = AppColors.TextPrimary;
        btn.FlatAppearance.BorderColor = AppColors.Border;
        btn.FlatAppearance.MouseOverBackColor = AppColors.Hover;
        btn.FlatAppearance.MouseDownBackColor = AppColors.Border;
    }

    private void UpdateThemeButtonIcon()
    {
        bool isDark = ThemeManager.Instance.IsDarkTheme;
        var icon = isDark ? ColorIcons.Sun : ColorIcons.Moon;
        btnTheme.Text = icon.Glyph;
        btnTheme.ForeColor = icon.Color;
        _themeToolTip.SetToolTip(btnTheme, isDark ? "Switch to Light Mode" : "Switch to Dark Mode");
    }

    /// <summary>
    /// The watch-status label carries a state color (success/warning/muted/error) that is set
    /// by whichever status update ran last; carry that state across to the new palette.
    /// </summary>
    private static void RemapStatusColor(Label label, AppTheme previous)
    {
        var c = label.ForeColor.ToArgb();
        label.ForeColor =
            c == previous.Success.ToArgb() ? AppColors.Success :
            c == previous.Warning.ToArgb() ? AppColors.Warning :
            c == previous.Error.ToArgb() ? AppColors.Error :
            AppColors.TextMuted;
    }

    #endregion

    #region Native (title bar, scrollbars)

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    private void ApplyNativeTheme()
    {
        if (!IsHandleCreated) return;
        bool isDark = ThemeManager.Instance.IsDarkTheme;

        try
        {
            int value = isDark ? 1 : 0;
            if (DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));

            // Dark scrollbars: the recent-searches list scrolls natively, the grid through
            // its own ScrollBar child controls.
            string themeName = isDark ? "DarkMode_Explorer" : "Explorer";
            var scrollHosts = dgvResults.Controls.OfType<ScrollBar>().Cast<Control>().Append(flpRecentSearches);
            foreach (var ctl in scrollHosts.Where(c => c.IsHandleCreated))
                SetWindowTheme(ctl.Handle, themeName, null);
        }
        catch
        {
            // Cosmetic only - older Windows builds without these APIs keep the light chrome.
        }
    }

    #endregion
}
