using System.Diagnostics;
using AnythingSearch.Helper;
using static AnythingSearch.Forms.MainForm;

namespace AnythingSearch.UserControls.About;

/// <summary>
/// About screen footer: website link, support address (with a copy action) and the
/// developer/copyright lines. Ported from NetSpeedMeterPlus's AboutFooterControl. Closing is
/// left to the dialog's own title bar - a second Close button below the fold only added noise.
/// </summary>
public class AboutFooterControl : Panel
{
    private readonly float _dpiScale;
    private readonly ToolTip _tips = new();

    public AboutFooterControl(float dpiScale, int width)
    {
        _dpiScale = dpiScale;

        Width = width;
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, S(12));

        Build();
    }

    private int S(int value) => (int)Math.Round(value * _dpiScale);

    private void Build()
    {
        int y = 0;

        Controls.Add(new Panel
        {
            Size = new Size(Width, S(1)),
            Location = new Point(0, y),
            BackColor = AppColors.Border
        });
        y += S(14);

        y = AddWebsite(y);
        y = AddSupport(y);

        Controls.Add(CenteredLabel($"Developed by {AboutContent.Developer}",
            new Font("Segoe UI", 8.5f, FontStyle.Bold), AppColors.TextPrimary, y));
        y += S(20);

        Controls.Add(CenteredLabel(AppConfig.Copyright,
            new Font("Segoe UI", 8f), AppColors.TextSecondary, y));

        // Breathing room under the last line - the stack's own padding alone left the
        // copyright sitting on the dialog's bottom edge.
        Height = y + S(18) + S(16);
    }

    /// <summary>Globe + link, centred as one block.</summary>
    private int AddWebsite(int y)
    {
        var globe = RowIcon(ColorIcons.Website);

        var link = new LinkLabel
        {
            Text = AboutContent.WebsiteLabel,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize = true,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            LinkBehavior = LinkBehavior.HoverUnderline,
            LinkColor = AppColors.Link,
            ActiveLinkColor = AppColors.Link,
            VisitedLinkColor = AppColors.Link
        };
        link.LinkClicked += (_, _) => OpenUrl(AboutContent.WebsiteUrl);

        int labelWidth = TextRenderer.MeasureText(link.Text, link.Font).Width;
        int x = (Width - (globe.Width + S(4) + labelWidth)) / 2;
        globe.Location = new Point(x, y);
        link.Location = new Point(globe.Right + S(4), y + S(2));

        Controls.Add(globe);
        Controls.Add(link);
        return y + S(26);
    }

    /// <summary>Envelope + support address + copy action, centred as one block.</summary>
    private int AddSupport(int y)
    {
        var mail = RowIcon(ColorIcons.Mail);

        var lblSupport = new Label
        {
            Text = AboutContent.SupportEmail,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = AppColors.TextSecondary,
            AutoSize = true,
            UseMnemonic = false,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        lblSupport.Click += (_, _) => OpenUrl($"mailto:{AboutContent.SupportEmail}");

        var btnCopy = new Button
        {
            Text = ColorIcons.CopyPath.Glyph,
            Font = new Font(ColorIcons.IconFontName, 9f),
            ForeColor = ColorIcons.CopyPath.Color,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            Size = new Size(S(24), S(22)),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        btnCopy.FlatAppearance.BorderSize = 0;
        btnCopy.FlatAppearance.MouseOverBackColor = AppColors.Hover;
        btnCopy.Click += (_, _) => CopyEmail(btnCopy);
        _tips.SetToolTip(btnCopy, "Copy the support address");

        // Measured rather than read off the label: AutoSize width isn't settled until the
        // label has a parent.
        int labelWidth = TextRenderer.MeasureText(lblSupport.Text, lblSupport.Font).Width;
        int x = (Width - (mail.Width + S(4) + labelWidth + S(2) + btnCopy.Width)) / 2;

        mail.Location = new Point(x, y);
        lblSupport.Location = new Point(mail.Right + S(4), y + S(2));
        btnCopy.Location = new Point(lblSupport.Left + labelWidth + S(2), y - S(1));

        Controls.Add(mail);
        Controls.Add(lblSupport);
        Controls.Add(btnCopy);
        return y + S(30);
    }

    private void CopyEmail(Control anchor)
    {
        try
        {
            Clipboard.SetText(AppConfig.SupportEmail);
            _tips.Show("Copied!", anchor, 0, -anchor.Height, 1500);
        }
        catch (Exception ex)
        {
            Logger.Log($"AboutFooterControl: copy email failed: {ex.Message}");
            _tips.Show("Clipboard busy - try again.", anchor, 0, -anchor.Height, 2000);
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / mail client registered - nothing useful to report */ }
    }

    private Label RowIcon(ColorIcon icon) => new()
    {
        Text = icon.Glyph,
        Font = new Font(ColorIcons.IconFontName, 10f),
        ForeColor = icon.Color,
        BackColor = Color.Transparent,
        AutoSize = false,
        Size = new Size(S(18), S(20)),
        TextAlign = ContentAlignment.MiddleCenter
    };

    private Label CenteredLabel(string text, Font font, Color color, int y) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        BackColor = Color.Transparent,
        AutoSize = false,
        UseMnemonic = false,
        Size = new Size(Width, S(18)),
        Location = new Point(0, y),
        TextAlign = ContentAlignment.MiddleCenter
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
