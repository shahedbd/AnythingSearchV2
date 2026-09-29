using AnythingSearch.Helper;
using AnythingSearch.Services;
using AnythingSearch.UserControls.About;

namespace AnythingSearch.Forms;

/// <summary>
/// The one About dialog, for both editions. Ported from NetSpeedMeterPlus's AboutForm: a
/// scrolling stack of header, feature cards and footer (UserControls/About). What it shows
/// comes from AboutTierPlan.ForCurrentTier(), so a Free user sees what is included plus the
/// Go Pro offer, and a Pro owner sees what the purchase covers with no upsell at all.
///
/// Colors come from MainForm.AppColors, which follows the active light/dark theme. The theme
/// can't change while this modal dialog is open (the toggle is on the main window), so it is
/// read once per build rather than listened to.
/// </summary>
public partial class AboutForm : Form
{
    private readonly float _dpiScale;
    private FlowLayoutPanel _stack = null!;

    public AboutForm()
    {
        // Same DPI model as MainForm/GoProForm: 96-dpi baseline, every pixel through S().
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F);
        _dpiScale = DeviceDpi / 96f;

        ConfigureForm();
        BuildLayout();

        HandleCreated += (_, _) =>
        {
            bool isDark = ThemeManager.Instance.IsDarkTheme;
            NativeTheme.ApplyTitleBar(this, isDark);
            NativeTheme.ApplyScrollBars([_stack], isDark);
        };
    }

    private int S(int value) => (int)Math.Round(value * _dpiScale);

    // ─────────────────────────────────────────────────────────────────────
    // FORM
    // ─────────────────────────────────────────────────────────────────────

    private void ConfigureForm()
    {
        // The system title bar carries the caption and the close button; the footer
        // deliberately has no Close button of its own (see AboutFooterControl).
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        ClientSize = new Size(S(560), S(720));
        BackColor = MainForm.AppColors.Background;
        Text = $"About {AppConfig.AppName}";
        Name = "AboutForm";
        Icon = File.Exists(AppConfig.FaviconPath) ? new Icon(AppConfig.FaviconPath) : null;

        _stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = MainForm.AppColors.Background,
            Padding = new Padding(S(24), S(16), S(24), S(16))
        };
        Controls.Add(_stack);
    }

    // ─────────────────────────────────────────────────────────────────────
    // LAYOUT
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>(Re)builds the stack for the current edition - called again after Go Pro,
    /// since a purchase there changes which cards and badge this screen shows.</summary>
    private void BuildLayout()
    {
        var plan = AboutTierPlan.ForCurrentTier();

        _stack.SuspendLayout();
        foreach (Control old in _stack.Controls.Cast<Control>().ToList())
            old.Dispose();

        // Content width leaves room for the scrollbar so nothing reflows when an edition
        // shows a longer list.
        int width = ClientSize.Width - S(48) - SystemInformation.VerticalScrollBarWidth;

        var header = new AboutHeaderControl(_dpiScale, width, plan.EditionLabel, plan.EditionColor);
        header.EditionBadgeClicked += (_, _) => OpenGoPro();
        _stack.Controls.Add(header);

        foreach (var spec in plan.Cards)
        {
            var card = new AboutFeatureCard(_dpiScale, width, spec);
            card.CtaClicked += (_, _) => OpenGoPro();
            _stack.Controls.Add(card);
        }

        _stack.Controls.Add(new AboutFooterControl(_dpiScale, width));
        _stack.ResumeLayout();
    }

    /// <summary>
    /// Opens at the top. WinForms scrolls an AutoScroll container to whichever child holds
    /// focus, and the first selectable control here is the CTA further down the stack - which
    /// would land the dialog mid-page on open.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScrollToTop();
    }

    private void ScrollToTop()
    {
        ActiveControl = null;
        _stack.AutoScrollPosition = Point.Empty;
        _stack.PerformLayout();
    }
}
