using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AnythingSearch.Helper;

/// <summary>A Segoe MDL2 Assets glyph paired with the color it is always drawn in.</summary>
public readonly record struct ColorIcon(string Glyph, Color Color);

/// <summary>
/// The app's colorful icon set. WinForms draws text through GDI, which renders emoji in
/// monochrome, so icons are Segoe MDL2 Assets glyphs painted in a fixed per-icon color instead.
/// The colors are mid-saturation so they read on both the light and the dark theme.
/// Labels/buttons can show a glyph directly (IconFont + ForeColor); menus and text buttons
/// take a bitmap from <see cref="Render"/>.
/// </summary>
public static class ColorIcons
{
    public const string IconFontName = "Segoe MDL2 Assets";

    private static readonly Color Blue = Color.FromArgb(33, 150, 243);
    private static readonly Color Green = Color.FromArgb(16, 185, 129);
    private static readonly Color Orange = Color.FromArgb(245, 158, 11);
    private static readonly Color Purple = Color.FromArgb(139, 92, 246);
    private static readonly Color Teal = Color.FromArgb(20, 184, 166);
    private static readonly Color Cyan = Color.FromArgb(6, 182, 212);
    private static readonly Color Red = Color.FromArgb(239, 68, 68);
    private static readonly Color Indigo = Color.FromArgb(99, 102, 241);
    private static readonly Color Amber = Color.FromArgb(251, 191, 36);

    // Header / search
    public static readonly ColorIcon Search = new("\uE721", Blue);
    public static readonly ColorIcon Clear = new("\uE711", Red);
    public static readonly ColorIcon Rebuild = new("\uE72C", Green);
    public static readonly ColorIcon Build = new("\uE90F", Orange);
    public static readonly ColorIcon Indexing = new("\uE823", Amber);
    public static readonly ColorIcon Settings = new("\uE713", Purple);
    public static readonly ColorIcon Moon = new("\uE708", Indigo);
    public static readonly ColorIcon Sun = new("\uE706", Amber);

    // Menus / tray
    public static readonly ColorIcon OpenApp = new("\uE8B7", Orange);
    public static readonly ColorIcon MinimizeToTray = new("\uE896", Teal);
    public static readonly ColorIcon Exit = new("\uE7E8", Red);
    public static readonly ColorIcon About = new("\uE946", Blue);
    public static readonly ColorIcon Update = new("\uE895", Green);
    public static readonly ColorIcon Website = new("\uE774", Cyan);
    public static readonly ColorIcon Status = new("\uE9D2", Purple);

    // Go Pro
    public static readonly ColorIcon ProStar = new("\uE735", Amber);
    public static readonly ColorIcon CheckMark = new("\uE73E", Green);
    public static readonly ColorIcon Mail = new("\uE715", Blue);

    // About - feature rows
    public static readonly ColorIcon AutoWatch = new("\uE7B3", Teal);
    public static readonly ColorIcon History = new("\uE81C", Cyan);
    public static readonly ColorIcon Lightning = new("\uE945", Amber);

    // Results context menu
    public static readonly ColorIcon Open = new("\uE8E5", Blue);
    public static readonly ColorIcon OpenLocation = new("\uE838", Orange);
    public static readonly ColorIcon CopyPath = new("\uE8C8", Teal);
    public static readonly ColorIcon CopyName = new("\uE8AC", Cyan);
    public static readonly ColorIcon Properties = new("\uE946", Indigo);
    public static readonly ColorIcon Delete = new("\uE74D", Red);

    private static readonly Dictionary<(ColorIcon, int), Bitmap> Cache = new();

    /// <summary>Menu/button icon edge length in pixels for the given control's DPI.</summary>
    public static int SizeFor(Control control) => (int)(16 * control.DeviceDpi / 96f);

    /// <summary>
    /// Renders the glyph to a transparent square bitmap. Cached for the app's lifetime (a few
    /// dozen tiny bitmaps), so callers must not dispose the result.
    /// </summary>
    public static Bitmap Render(ColorIcon icon, int sizePx)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((icon, sizePx), out var cached))
                return cached;

            var bmp = new Bitmap(sizePx, sizePx);
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font(IconFontName, sizePx * 0.8f, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(icon.Color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(icon.Glyph, font, brush, new RectangleF(0, 0, sizePx, sizePx), format);
            }

            Cache[(icon, sizePx)] = bmp;
            return bmp;
        }
    }

    /// <summary>Puts a colored icon before a button's text.</summary>
    public static void SetButtonIcon(Button button, ColorIcon icon, string text)
    {
        button.Text = " " + text;
        button.Image = Render(icon, SizeFor(button));
        button.ImageAlign = ContentAlignment.MiddleCenter;
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
    }

    /// <summary>A menu item with a colored icon.</summary>
    public static ToolStripMenuItem MenuItem(string text, ColorIcon icon, Control owner,
        EventHandler? onClick = null, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(text, Render(icon, SizeFor(owner)), onClick);
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        return item;
    }
}
