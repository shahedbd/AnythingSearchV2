using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// Theme colors and styling for MainForm
/// </summary>
public partial class MainForm
{
    /// <summary>
    /// Application color palette - Modern Windows 11 style. Every member resolves against
    /// ThemeManager.CurrentTheme, so reads always return the active (light or dark) color.
    /// </summary>
    public static class AppColors
    {
        private static AppTheme T => ThemeManager.Instance.CurrentTheme;

        // Primary colors
        public static Color Primary => T.Primary;
        public static Color PrimaryDark => T.PrimaryDark;
        public static Color PrimaryLight => T.PrimaryLight;
        public static Color Link => T.Link;
        public static Color SelectionBack => T.SelectionBack;

        // Background colors
        public static Color Background => T.Background;
        public static Color Surface => T.Surface;
        public static Color SurfaceAlt => T.SurfaceAlt;
        public static Color StatusBar => T.StatusBar;
        public static Color GridHeader => T.GridHeader;
        public static Color Hover => T.Hover;
        public static Color Selected => T.Selected;
        public static Color DangerHover => T.DangerHover;

        // Border colors
        public static Color Border => T.Border;
        public static Color BorderFocus => T.BorderFocus;

        // Text colors
        public static Color TextPrimary => T.TextPrimary;
        public static Color TextSecondary => T.TextSecondary;
        public static Color TextMuted => T.TextMuted;
        public static Color DangerText => T.DangerText;

        // Status colors
        public static Color Success => T.Success;
        public static Color Warning => T.Warning;
        public static Color Error => T.Error;
    }

    /// <summary>
    /// Creates a modern styled button
    /// </summary>
    private Button CreateModernButton(string text, Point location, Size size)
    {
        var btn = new Button
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 1;
        StyleModernButton(btn);
        return btn;
    }

    /// <summary>
    /// Paint handler for panel borders
    /// </summary>
    private void Panel_PaintBorder(object? sender, PaintEventArgs e)
    {
        if (sender is Panel panel)
        {
            using var pen = new Pen(AppColors.Border, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        }
    }

    /// <summary>
    /// Creates a rounded rectangle path
    /// </summary>
    private System.Drawing.Drawing2D.GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Modern ToolStrip Renderer for menus
/// </summary>
public class ModernToolStripRenderer : ToolStripProfessionalRenderer
{
    public ModernToolStripRenderer() : base(new ModernColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected && e.Item.Enabled)
        {
            using var brush = new SolidBrush(MainForm.AppColors.Selected);
            e.Graphics.FillRectangle(brush, new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height));
        }
        else
        {
            base.OnRenderMenuItemBackground(e);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? MainForm.AppColors.TextPrimary : MainForm.AppColors.TextMuted;
        base.OnRenderItemText(e);
    }
}

/// <summary>
/// Color table for modern menu styling
/// </summary>
public class ModernColorTable : ProfessionalColorTable
{
    private static Color Surface => MainForm.AppColors.Surface;

    public override Color MenuBorder => MainForm.AppColors.Border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => MainForm.AppColors.Selected;
    public override Color MenuItemPressedGradientBegin => MainForm.AppColors.Hover;
    public override Color MenuItemPressedGradientEnd => MainForm.AppColors.Hover;
    public override Color MenuStripGradientBegin => Surface;
    public override Color MenuStripGradientEnd => Surface;
    public override Color ToolStripDropDownBackground => Surface;
    public override Color ImageMarginGradientBegin => Surface;
    public override Color ImageMarginGradientMiddle => Surface;
    public override Color ImageMarginGradientEnd => Surface;
    public override Color SeparatorDark => MainForm.AppColors.Border;
    public override Color SeparatorLight => Surface;
}
