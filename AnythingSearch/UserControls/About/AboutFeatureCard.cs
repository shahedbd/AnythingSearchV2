using AnythingSearch.Helper;
using static AnythingSearch.Forms.MainForm;

namespace AnythingSearch.UserControls.About;

/// <summary>
/// One About-screen card: accented header, a list of feature rows and an optional
/// call-to-action button. Ported from NetSpeedMeterPlus's AboutFeatureCard; icons are
/// ColorIcons glyphs drawn in their own colors, so every row stays colorful in both themes.
/// </summary>
public class AboutFeatureCard : Panel
{
    private readonly float _dpiScale;
    private readonly Color _accent;

    /// <summary>Raised when the card's call-to-action is clicked.</summary>
    public event EventHandler? CtaClicked;

    public AboutFeatureCard(float dpiScale, int width, AboutCardSpec spec)
    {
        _dpiScale = dpiScale;
        _accent = spec.Icon.Color;

        Width = width;
        BackColor = AppColors.Surface;
        Margin = new Padding(0, 0, 0, S(14));
        Paint += (_, e) =>
        {
            using var pen = new Pen(AppColors.Border, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };

        Build(spec);
    }

    private int S(int value) => (int)Math.Round(value * _dpiScale);

    private void Build(AboutCardSpec spec)
    {
        int headerH = S(38);
        AddHeader(spec.Icon, spec.Title, headerH);

        int y = headerH + S(8);

        foreach (var (icon, text) in spec.Rows)
        {
            AddRow(icon, text, y);
            y += S(26);
        }

        if (!string.IsNullOrWhiteSpace(spec.Note))
        {
            Controls.Add(new Label
            {
                Text = spec.Note,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                ForeColor = AppColors.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize = false,
                UseMnemonic = false,
                Size = new Size(Width - S(32), S(34)),
                Location = new Point(S(16), y + S(2)),
                TextAlign = ContentAlignment.TopLeft
            });
            y += S(38);
        }

        if (!string.IsNullOrWhiteSpace(spec.CtaText))
        {
            AddCta(spec.CtaText, y + S(4));
            y += S(46);
        }

        Height = y + S(10);
    }

    private void AddHeader(ColorIcon icon, string title, int headerH)
    {
        var header = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(Width, headerH),
            BackColor = Color.FromArgb(28, _accent)
        };

        // Left accent bar
        header.Controls.Add(new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(S(4), headerH),
            BackColor = _accent
        });

        header.Controls.Add(GlyphLabel(icon, 11f, new Point(S(12), 0), new Size(S(26), headerH)));

        header.Controls.Add(new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = _accent,
            BackColor = Color.Transparent,
            AutoSize = false,
            UseMnemonic = false,
            Size = new Size(Width - S(44), headerH),
            Location = new Point(S(42), 0),
            TextAlign = ContentAlignment.MiddleLeft
        });

        Controls.Add(header);
    }

    private void AddRow(ColorIcon icon, string text, int y)
    {
        Controls.Add(GlyphLabel(icon, 10f, new Point(S(14), y), new Size(S(24), S(24))));

        Controls.Add(new Label
        {
            Text = text,
            UseMnemonic = false,   // feature strings contain real ampersands
            Font = new Font("Segoe UI", 9f),
            ForeColor = AppColors.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = false,
            Size = new Size(Width - S(52), S(24)),
            Location = new Point(S(44), y),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        });
    }

    /// <summary>A Segoe MDL2 glyph drawn in the icon's own color.</summary>
    private static Label GlyphLabel(ColorIcon icon, float size, Point location, Size box) => new()
    {
        Text = icon.Glyph,
        Font = new Font(ColorIcons.IconFontName, size),
        ForeColor = icon.Color,
        BackColor = Color.Transparent,
        AutoSize = false,
        Location = location,
        Size = box,
        TextAlign = ContentAlignment.MiddleCenter
    };

    /// <summary>
    /// The card's call to action - a flat button in the card's accent, sized to its label and
    /// centred (a full-width button splits the star and the words across its two halves).
    /// Dark text: the accent is the light Pro amber, same as the Go Pro badge.
    /// </summary>
    private void AddCta(string text, int y)
    {
        var btn = new Button
        {
            Text = "★  " + text,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.Black,
            BackColor = _accent,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        int content = TextRenderer.MeasureText(btn.Text, btn.Font).Width + S(70);
        btn.Size = new Size(Math.Clamp(content, S(200), Width - S(32)), S(38));
        btn.Location = new Point((Width - btn.Width) / 2, y);

        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(_accent, 0.15f);
        btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(_accent, 0.1f);
        btn.Click += (_, e) => CtaClicked?.Invoke(this, e);

        Controls.Add(btn);
    }
}
