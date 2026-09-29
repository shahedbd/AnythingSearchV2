using AnythingSearch.Helper;
using static AnythingSearch.Forms.MainForm;

namespace AnythingSearch.Forms;

/// <summary>Small control factories shared by GoProForm's Free and Pro layouts.</summary>
public sealed partial class GoProForm
{
    // ══════════════════════════════════════════════════════════════════
    //  FACTORIES
    // ══════════════════════════════════════════════════════════════════

    private Control MakeBadge(string glyph, Color color)
    {
        int size = Scale(72);
        var badge = new Panel
        {
            Size = new Size(size, size),
            BackColor = Color.FromArgb(38, color),
            Margin = new Padding(0, 0, 0, Scale(12))
        };
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            path.AddEllipse(0, 0, size, size);
            badge.Region = new Region(path);
        }
        badge.Controls.Add(new Label
        {
            Text = glyph,
            Font = new Font(ColorIcons.IconFontName, 26f),
            ForeColor = color,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        });
        return badge;
    }

    private static Label MakeLabel(string text, float size, FontStyle style, Color color, int bottom) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", size, style),
        ForeColor = color,
        BackColor = Color.Transparent,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleCenter,
        Margin = new Padding(0, 0, 0, bottom)
    };

    private Button MakeButton(string text, Color back, Color fore)
    {
        var btn = new Button
        {
            Text = text,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Size = new Size(Scale(260), Scale(44)),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, Scale(8))
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.15f);
        btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.1f);
        return btn;
    }

    private static LinkLabel MakeLink(string text, EventHandler onClick)
    {
        var link = new LinkLabel
        {
            Text = text,
            Font = new Font("Segoe UI", 9.5f),
            AutoSize = true,
            LinkColor = AppColors.Link,
            ActiveLinkColor = AppColors.PrimaryDark,
            BackColor = Color.Transparent,
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        link.Click += onClick;
        return link;
    }

    private static FlowLayoutPanel MakeRow(params Control[] children)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent
        };
        row.Controls.AddRange(children);
        return row;
    }
}
