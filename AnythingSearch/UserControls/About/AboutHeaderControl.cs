using AnythingSearch.Helper;
using static AnythingSearch.Forms.MainForm;

namespace AnythingSearch.UserControls.About;

/// <summary>
/// About screen header: app icon, name, version and edition badges, tagline. Ported from
/// NetSpeedMeterPlus's AboutHeaderControl. The edition badge is clickable - it opens Go Pro
/// (Free) or the "You're Pro" dialog (Pro), as the old About dialog's badge did.
/// </summary>
public class AboutHeaderControl : Panel
{
    /// <summary>
    /// App logo, loaded once and shared - a PictureBox does not dispose its Image, so loading
    /// per open would leak a handle every time the dialog is shown.
    /// </summary>
    private static readonly Lazy<Image?> AppIcon = new(() =>
    {
        try
        {
            return File.Exists(AppConfig.AppIconPath) ? Image.FromFile(AppConfig.AppIconPath) : null;
        }
        catch
        {
            return null;   // decorative - a missing asset must not break the dialog
        }
    });

    private readonly float _dpiScale;

    /// <summary>Raised when the edition badge is clicked.</summary>
    public event EventHandler? EditionBadgeClicked;

    /// <param name="editionLabel">"FREE · Go Pro" or "★ PRO".</param>
    public AboutHeaderControl(float dpiScale, int width, string editionLabel, Color editionColor)
    {
        _dpiScale = dpiScale;

        Width = width;
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, S(6));

        Build(editionLabel, editionColor);
    }

    private int S(int value) => (int)Math.Round(value * _dpiScale);

    private void Build(string editionLabel, Color editionColor)
    {
        int y = S(4);
        int iconSize = S(64);

        Controls.Add(new PictureBox
        {
            Image = AppIcon.Value,
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(iconSize, iconSize),
            Location = new Point((Width - iconSize) / 2, y),
            BackColor = Color.Transparent
        });
        y += iconSize + S(8);

        var lblAppName = CenteredLabel(AppConfig.AppName,
            new Font("Segoe UI", 20f, FontStyle.Bold), AppColors.TextPrimary, y);
        Controls.Add(lblAppName);
        y += lblAppName.Height + S(6);

        // ── Version + edition badges, side by side ────────────────────────
        var versionBadge = BuildBadge(AppConfig.AppVersion.Replace("Version ", "v"), AppColors.TextSecondary);
        var editionBadge = BuildBadge(editionLabel, editionColor);
        editionBadge.Cursor = Cursors.Hand;
        foreach (Control c in editionBadge.Controls.Cast<Control>().Append(editionBadge))
            c.Click += (_, e) => EditionBadgeClicked?.Invoke(this, e);

        int gap = S(8);
        int startX = (Width - (versionBadge.Width + gap + editionBadge.Width)) / 2;
        versionBadge.Location = new Point(startX, y);
        editionBadge.Location = new Point(versionBadge.Right + gap, y);

        Controls.Add(versionBadge);
        Controls.Add(editionBadge);
        y += editionBadge.Height + S(14);

        var lblTagline = CenteredLabel(AboutContent.Tagline,
            new Font("Segoe UI", 9.5f), AppColors.TextPrimary, y);
        Controls.Add(lblTagline);
        y += lblTagline.Height + S(6);

        var lblSubTagline = CenteredLabel(AboutContent.SubTagline,
            new Font("Segoe UI", 8.5f), AppColors.TextSecondary, y);
        Controls.Add(lblSubTagline);
        y += lblSubTagline.Height + S(10);

        Height = y;
    }

    /// <summary>Pill badge - tinted background, colored bold text.</summary>
    private Panel BuildBadge(string text, Color accent)
    {
        var font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        int width = TextRenderer.MeasureText(text, font).Width + S(20);

        var badge = new Panel
        {
            Size = new Size(width, S(22)),
            BackColor = Color.FromArgb(38, accent)
        };

        badge.Controls.Add(new Label
        {
            Text = text,
            Font = font,
            ForeColor = accent,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        });

        return badge;
    }

    /// <summary>
    /// Full-width centred label, measured with word wrap so a long tagline gets the lines it
    /// needs instead of being clipped to one.
    /// </summary>
    private Label CenteredLabel(string text, Font font, Color color, int y)
    {
        int height = TextRenderer.MeasureText(
            text, font, new Size(Width, 0), TextFormatFlags.WordBreak).Height + S(6);

        return new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoSize = false,
            Size = new Size(Width, height),
            Location = new Point(0, y),
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false
        };
    }
}
