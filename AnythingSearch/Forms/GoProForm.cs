using AnythingSearch.Helper;
using AnythingSearch.Services;
using AnythingSearch.Services.Store;
using static AnythingSearch.Forms.MainForm;

namespace AnythingSearch.Forms;

/// <summary>
/// The Go Pro dialog (Free users) and its "You're Pro" counterpart (Pro owners), described in
/// Doc/Requirements/05_Go_Pro_Design.md §5-§6. Built in code; every element sits in one
/// centred column between two spring rows - the layout trick from NetSpeedMeterPlus's
/// SubscriptionPromptControl. Purchase, restore and price loading live in GoProForm.Actions.cs.
/// </summary>
public sealed partial class GoProForm : Form
{
    private static readonly Color Accent = ColorIcons.ProStar.Color;

    /// <summary>(Free title, Pro title, detail) - the four benefits the add-on sells.</summary>
    private static readonly (string Title, string ProTitle, string Detail)[] Benefits =
    {
        ("One-time payment", "Paid once - yours for life", "No subscription. No renewals."),
        ("No ads", "No ads", "No promo pages or browser tabs, ever."),
        ("Lifetime updates", "Lifetime updates", "Every future version included."),
        ("Lifetime support", "Lifetime support", "Priority email support from the developer.")
    };

    /// <summary>
    /// Microsoft Store badge, loaded once and shared - a PictureBox does not dispose its Image,
    /// so loading per build would leak a handle on every open and every theme switch.
    /// </summary>
    private static readonly Lazy<Image?> StoreBadge = new(() =>
    {
        try
        {
            return File.Exists(AppConfig.MicrosoftStoreAppIconPath)
                ? Image.FromFile(AppConfig.MicrosoftStoreAppIconPath)
                : null;
        }
        catch
        {
            return null;   // decorative - a missing asset must not break the dialog
        }
    });

    private readonly float _dpiScale;
    private TableLayoutPanel? _stack;
    private Label? _lblPrice;
    private Label? _lblError;
    private Button? _btnUpgrade;
    private LinkLabel? _lnkRestore;

    public GoProForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F);
        _dpiScale = DeviceDpi / 96f;

        ClientSize = new Size(Scale(460), Scale(600));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        if (File.Exists(AppConfig.FaviconPath))
            Icon = new Icon(AppConfig.FaviconPath);

        BuildLayout();

        ThemeManager.Instance.ThemeChanged += OnThemeChanged;
        FormClosed += (_, _) => ThemeManager.Instance.ThemeChanged -= OnThemeChanged;

        if (!ProLicenseManager.Instance.IsPro)
            _ = LoadPriceAsync();
    }

    private int Scale(int value) => (int)(value * _dpiScale);

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        if (!IsDisposed)
            BeginInvoke(new Action(BuildLayout));
    }

    // ══════════════════════════════════════════════════════════════════
    //  LAYOUT
    // ══════════════════════════════════════════════════════════════════

    /// <summary>(Re)builds the whole dialog for the current licence and theme.</summary>
    private void BuildLayout()
    {
        bool isPro = ProLicenseManager.Instance.IsPro;
        Text = isPro ? $"{AppConfig.AppName} Pro" : "Go Pro";
        BackColor = AppColors.Background;

        var old = _stack;
        _stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(Scale(20))
        };
        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var content = isPro ? BuildProContent() : BuildFreeContent();

        _stack.RowCount = content.Count + 2;
        _stack.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));   // top spring
        for (int i = 0; i < content.Count; i++)
        {
            _stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content[i].Anchor = AnchorStyles.None;
            _stack.Controls.Add(content[i], 0, i + 1);
        }
        _stack.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));   // bottom spring

        SuspendLayout();
        Controls.Add(_stack);
        if (old != null)
        {
            Controls.Remove(old);
            old.Dispose();
        }
        ResumeLayout();
    }

    private List<Control> BuildFreeContent()
    {
        _btnUpgrade = MakeButton("★  Upgrade to Pro", Accent, Color.Black);
        _btnUpgrade.Click += BtnUpgrade_Click;
        AcceptButton = _btnUpgrade;

        _lblPrice = MakeLabel(_price, 24f, FontStyle.Bold, AppColors.TextPrimary, 0);
        _lblError = MakeLabel(string.Empty, 9f, FontStyle.Regular, AppColors.Error, Scale(8));
        _lblError.Visible = false;

        _lnkRestore = MakeLink("Restore purchase", LnkRestore_Click);

        return new List<Control>
        {
            MakeBadge(ColorIcons.ProStar.Glyph, Accent),
            MakeLabel($"{AppConfig.AppName}  PRO", 18f, FontStyle.Bold, AppColors.TextPrimary, Scale(2)),
            MakeLabel("Pay once. Own it for life.", 10f, FontStyle.Regular, AppColors.TextSecondary, Scale(16)),
            BuildBenefitsCard(withDetails: true),
            _lblPrice,
            MakeLabel("one-time payment", 9f, FontStyle.Regular, AppColors.TextSecondary, Scale(14)),
            _btnUpgrade,
            _lblError,
            MakeRow(_lnkRestore, MakeLabel("|", 9f, FontStyle.Regular, AppColors.TextMuted, 0),
                    MakeLink("Not now", (_, _) => Close())),
            BuildTrustLine()
        };
    }

    private List<Control> BuildProContent()
    {
        _btnUpgrade = null;
        _lblPrice = null;
        _lblError = null;
        _lnkRestore = null;

        var btnSupport = MakeButton("✉  Contact Pro Support", AppColors.Primary, Color.White);
        btnSupport.Click += BtnSupport_Click;
        AcceptButton = btnSupport;

        return new List<Control>
        {
            MakeBadge(ColorIcons.CheckMark.Glyph, AppColors.Success),
            MakeLabel("You're Pro!", 18f, FontStyle.Bold, AppColors.TextPrimary, Scale(2)),
            MakeLabel("Thank you for your support.", 10f, FontStyle.Regular, AppColors.TextSecondary, Scale(16)),
            BuildBenefitsCard(withDetails: false),
            btnSupport,
            MakeLink("Close", (_, _) => Close())
        };
    }

    /// <summary>Bordered card listing the four benefits, each with a green tick.</summary>
    private Control BuildBenefitsCard(bool withDetails)
    {
        var card = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(Scale(360), 0),
            BackColor = AppColors.Surface,
            Padding = new Padding(Scale(16), Scale(12), Scale(16), Scale(2)),
            Margin = new Padding(0, 0, 0, Scale(18))
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(AppColors.Border, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };

        foreach (var (title, proTitle, detail) in Benefits)
        {
            card.Controls.Add(new Label
            {
                Text = ColorIcons.CheckMark.Glyph,
                Font = new Font(ColorIcons.IconFontName, 11f),
                ForeColor = AppColors.Success,
                AutoSize = true,
                Margin = new Padding(0, Scale(3), Scale(8), 0)
            });

            var text = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, Scale(10))
            };
            text.Controls.Add(MakeLabel(withDetails ? title : proTitle, 10f, FontStyle.Bold, AppColors.TextPrimary, 0));
            if (withDetails)
                text.Controls.Add(MakeLabel(detail, 9f, FontStyle.Regular, AppColors.TextSecondary, 0));
            card.Controls.Add(text);
        }

        return card;
    }

    /// <summary>Microsoft Store badge + payment reassurance, right under the CTA.</summary>
    private Control BuildTrustLine()
    {
        var row = MakeRow();
        row.Margin = new Padding(0, Scale(14), 0, 0);

        if (StoreBadge.Value is Image badge)
        {
            row.Controls.Add(new PictureBox
            {
                Image = badge,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(Scale(28), Scale(28)),
                Margin = new Padding(0, Scale(2), Scale(8), 0)
            });
        }

        row.Controls.Add(MakeLabel("Secure payment by Microsoft Store.\nYour search features stay free, always.",
            8.5f, FontStyle.Regular, AppColors.TextSecondary, 0));
        return row;
    }
}
