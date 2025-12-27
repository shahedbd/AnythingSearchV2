namespace AnythingSearch.Forms;

/// <summary>
/// Theme colors and styling for MainForm
/// </summary>
public partial class MainForm
{
    /// <summary>
    /// Application color palette - Modern Windows 11 style
    /// </summary>
    public static class AppColors
    {
        // Primary colors
        public static readonly Color Primary = Color.FromArgb(0, 120, 212);
        public static readonly Color PrimaryDark = Color.FromArgb(0, 99, 177);
        public static readonly Color PrimaryLight = Color.FromArgb(0, 140, 240);

        // Background colors
        public static readonly Color Background = Color.FromArgb(249, 249, 249);
        public static readonly Color Surface = Color.White;
        public static readonly Color StatusBar = Color.FromArgb(243, 243, 243);
        public static readonly Color Hover = Color.FromArgb(243, 243, 243);
        public static readonly Color Selected = Color.FromArgb(232, 240, 254);

        // Border colors
        public static readonly Color Border = Color.FromArgb(229, 229, 229);
        public static readonly Color BorderFocus = Color.FromArgb(0, 120, 212);

        // Text colors
        public static readonly Color TextPrimary = Color.FromArgb(32, 32, 32);
        public static readonly Color TextSecondary = Color.FromArgb(96, 96, 96);
        public static readonly Color TextMuted = Color.FromArgb(136, 136, 136);

        // Status colors
        public static readonly Color Success = Color.FromArgb(16, 124, 16);
        public static readonly Color Warning = Color.FromArgb(255, 140, 0);
        public static readonly Color Error = Color.FromArgb(196, 43, 28);
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
            BackColor = AppColors.Surface,
            ForeColor = AppColors.TextPrimary,
            Font = new Font("Segoe UI", 9.5F),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = AppColors.Border;
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.MouseOverBackColor = AppColors.Hover;
        btn.FlatAppearance.MouseDownBackColor = AppColors.Border;
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
            using var brush = new SolidBrush(Color.FromArgb(232, 240, 254));
            e.Graphics.FillRectangle(brush, new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height));
        }
        else
        {
            base.OnRenderMenuItemBackground(e);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Color.FromArgb(32, 32, 32) : Color.FromArgb(160, 160, 160);
        base.OnRenderItemText(e);
    }
}

/// <summary>
/// Color table for modern menu styling
/// </summary>
public class ModernColorTable : ProfessionalColorTable
{
    public override Color MenuBorder => Color.FromArgb(229, 229, 229);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => Color.FromArgb(232, 240, 254);
    public override Color MenuStripGradientBegin => Color.White;
    public override Color MenuStripGradientEnd => Color.White;
    public override Color ToolStripDropDownBackground => Color.White;
    public override Color ImageMarginGradientBegin => Color.White;
    public override Color ImageMarginGradientMiddle => Color.White;
    public override Color ImageMarginGradientEnd => Color.White;
    public override Color SeparatorDark => Color.FromArgb(229, 229, 229);
    public override Color SeparatorLight => Color.White;
}
