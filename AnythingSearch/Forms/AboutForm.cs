using AnythingSearch.Database;
using System.Diagnostics;
using System.Reflection;

namespace AnythingSearch.Forms;

public partial class AboutForm : Form
{
    // Production Release Information
    private const string AppName = "Anything Search";
    private const string AppWebsite = "https://zerobytebd.com/anything-search";
    private const string AppEmail = "shahedbddev@gmail.com";
    private const string DeveloperName = "Zero Byte Software Solutions";
    private const string AppTagline = "Lightning-Fast File Search Utility";

    // Colors
    private static readonly Color PrimaryColor = Color.FromArgb(0, 120, 212);
    private static readonly Color PrimaryDark = Color.FromArgb(0, 90, 170);
    private static readonly Color PrimaryLight = Color.FromArgb(0, 140, 240);
    private static readonly Color BackgroundColor = Color.FromArgb(250, 250, 250);
    private static readonly Color CardColor = Color.White;
    private static readonly Color TextPrimary = Color.FromArgb(32, 32, 32);
    private static readonly Color TextSecondary = Color.FromArgb(96, 96, 96);
    private static readonly Color TextMuted = Color.FromArgb(128, 128, 128);

    // DPI scale factor
    private float _dpiScale = 1.0f;

    public AboutForm()
    {
        InitializeComponent();
        InitializeUI();
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        // CRITICAL: DPI Scaling for Microsoft Store compliance
        this.AutoScaleMode = AutoScaleMode.Dpi;
        this.AutoScaleDimensions = new SizeF(96F, 96F);
        this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        // Get DPI scale factor
        _dpiScale = this.DeviceDpi / 96f;

        // Scale the form size
        int baseWidth = 550;
        int baseHeight = 680;
        ClientSize = new Size((int)(baseWidth * _dpiScale), (int)(baseHeight * _dpiScale));

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "AboutForm";
        StartPosition = FormStartPosition.CenterParent;
        Text = $"About {AppName}";
        BackColor = BackgroundColor;

        // Load icon
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "favicon.ico");
            if (File.Exists(iconPath))
            {
                this.Icon = new Icon(iconPath);
            }
        }
        catch { }

        ResumeLayout(false);
    }

    // Helper method to scale values
    private int Scale(int value) => (int)(value * _dpiScale);

    private void InitializeUI()
    {
        int centerX = this.ClientSize.Width / 2;

        // ═══════════════════════════════════════════════════════════
        // HEADER SECTION
        // ═══════════════════════════════════════════════════════════

        // App Icon
        int iconSize = Scale(80);
        PictureBox iconLogo = new PictureBox
        {
            Image = GetAppIcon(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(iconSize, iconSize),
            Location = new Point(centerX - iconSize / 2, Scale(25)),
            BackColor = Color.Transparent
        };
        this.Controls.Add(iconLogo);

        // App Name
        Label lblAppName = new Label
        {
            Text = AppName,
            Font = new Font("Segoe UI", 24, FontStyle.Bold),
            ForeColor = PrimaryColor,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        int appNameWidth = TextRenderer.MeasureText(lblAppName.Text, lblAppName.Font).Width;
        lblAppName.Location = new Point(centerX - appNameWidth / 2, Scale(115));
        this.Controls.Add(lblAppName);

        // Version Badge
        int badgeWidth = Scale(120);
        int badgeHeight = Scale(24);
        Label lblVersion = new Label
        {
            Text = $"Version {CommonData.ApplicationVersion}",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = PrimaryColor,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(badgeWidth, badgeHeight),
            Padding = new Padding(Scale(8), Scale(4), Scale(8), Scale(4))
        };
        lblVersion.Location = new Point(centerX - badgeWidth / 2, Scale(155));
        this.Controls.Add(lblVersion);

        // Tagline
        Label lblTagline = new Label
        {
            Text = AppTagline,
            Font = new Font("Segoe UI", 10, FontStyle.Italic),
            ForeColor = TextSecondary,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        int taglineWidth = TextRenderer.MeasureText(lblTagline.Text, lblTagline.Font).Width;
        lblTagline.Location = new Point(centerX - taglineWidth / 2, Scale(190));
        this.Controls.Add(lblTagline);

        // ═══════════════════════════════════════════════════════════
        // FEATURES CARD
        // ═══════════════════════════════════════════════════════════

        Panel featuresCard = CreateCard(Scale(25), Scale(225), this.ClientSize.Width - Scale(50), Scale(220));

        Label lblFeaturesTitle = new Label
        {
            Text = "✨ Key Features",
            Font = new Font("Segoe UI Semibold", 11),
            ForeColor = PrimaryColor,
            AutoSize = true,
            Location = new Point(Scale(15), Scale(12)),
            BackColor = Color.Transparent
        };
        featuresCard.Controls.Add(lblFeaturesTitle);

        string[] features = new[]
        {
            "🔍  Instant search across millions of files",
            "⚡  Real-time results as you type",
            "📊  Smart indexing with background updates",
            "👁️  Auto-watch for file system changes",
            "🎨  Modern, clean Windows 11 style interface",
            "💾  Lightweight SQLite database",
            "🚀  Built with .NET 8.0 for optimal performance"
        };

        int yPos = Scale(42);
        int lineHeight = Scale(24);
        foreach (var feature in features)
        {
            var lbl = new Label
            {
                Text = feature,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = TextPrimary,
                AutoSize = true,
                Location = new Point(Scale(20), yPos),
                BackColor = Color.Transparent
            };
            featuresCard.Controls.Add(lbl);
            yPos += lineHeight;
        }

        this.Controls.Add(featuresCard);

        // ═══════════════════════════════════════════════════════════
        // CONTACT CARD
        // ═══════════════════════════════════════════════════════════

        Panel contactCard = CreateCard(Scale(25), Scale(455), this.ClientSize.Width - Scale(50), Scale(120));

        Label lblContactTitle = new Label
        {
            Text = "📬 Get in Touch",
            Font = new Font("Segoe UI Semibold", 11),
            ForeColor = PrimaryColor,
            AutoSize = true,
            Location = new Point(Scale(15), Scale(12)),
            BackColor = Color.Transparent
        };
        contactCard.Controls.Add(lblContactTitle);

        // Website Link
        var lblWebsite = CreateLinkLabel("🌐  Website:", AppWebsite, Scale(45));
        contactCard.Controls.Add(lblWebsite.Item1);
        contactCard.Controls.Add(lblWebsite.Item2);

        // Email Link
        var lblEmail = CreateLinkLabel("📧  Email:", AppEmail, Scale(72));
        contactCard.Controls.Add(lblEmail.Item1);
        contactCard.Controls.Add(lblEmail.Item2);

        this.Controls.Add(contactCard);

        // ═══════════════════════════════════════════════════════════
        // BUTTONS
        // ═══════════════════════════════════════════════════════════

        int btnWidth = Scale(160);
        int btnHeight = Scale(38);
        int btnY = Scale(590);
        int btnSpacing = Scale(10);

        // Check for Updates Button
        Button btnUpdate = new Button
        {
            Text = "🔄  Check for Updates",
            Font = new Font("Segoe UI", 9.5f),
            Size = new Size(btnWidth, btnHeight),
            Location = new Point(centerX - btnWidth - btnSpacing / 2, btnY),
            BackColor = CardColor,
            ForeColor = PrimaryColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnUpdate.FlatAppearance.BorderColor = PrimaryColor;
        btnUpdate.FlatAppearance.BorderSize = 1;
        btnUpdate.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 248, 255);
        btnUpdate.Click += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppWebsite) { UseShellExecute = true });
            }
            catch { }
        };
        this.Controls.Add(btnUpdate);

        // Close Button
        int closeBtnWidth = Scale(120);
        Button btnClose = new Button
        {
            Text = "✕  Close",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Size = new Size(closeBtnWidth, btnHeight),
            Location = new Point(centerX + btnSpacing / 2, btnY),
            BackColor = PrimaryColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnClose.FlatAppearance.BorderSize = 0;
        btnClose.FlatAppearance.MouseOverBackColor = PrimaryLight;
        btnClose.Click += (s, e) => this.Close();
        this.Controls.Add(btnClose);

        // ═══════════════════════════════════════════════════════════
        // FOOTER
        // ═══════════════════════════════════════════════════════════

        // Developer
        Label lblDeveloper = new Label
        {
            Text = $"Developed with ❤️ by {DeveloperName}",
            Font = new Font("Segoe UI", 9),
            ForeColor = TextSecondary,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        int devWidth = TextRenderer.MeasureText(lblDeveloper.Text, lblDeveloper.Font).Width;
        lblDeveloper.Location = new Point(centerX - devWidth / 2, Scale(640));
        this.Controls.Add(lblDeveloper);

        // Copyright
        Label lblCopyright = new Label
        {
            Text = $"© {DateTime.Now.Year} {DeveloperName}. All rights reserved.",
            Font = new Font("Segoe UI", 8),
            ForeColor = TextMuted,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        int copyWidth = TextRenderer.MeasureText(lblCopyright.Text, lblCopyright.Font).Width;
        lblCopyright.Location = new Point(centerX - copyWidth / 2, Scale(658));
        this.Controls.Add(lblCopyright);
    }

    private Panel CreateCard(int x, int y, int width, int height)
    {
        var card = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(width, height),
            BackColor = CardColor
        };
        card.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(229, 229, 229), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        return card;
    }

    private (Label, LinkLabel) CreateLinkLabel(string label, string linkText, int yPos)
    {
        var lbl = new Label
        {
            Text = label,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(Scale(20), yPos),
            BackColor = Color.Transparent
        };

        var link = new LinkLabel
        {
            Text = linkText,
            Font = new Font("Segoe UI", 9.5f),
            LinkColor = PrimaryColor,
            ActiveLinkColor = PrimaryDark,
            AutoSize = true,
            Location = new Point(Scale(105), yPos),
            BackColor = Color.Transparent
        };
        link.LinkClicked += (s, e) =>
        {
            try
            {
                if (linkText.Contains("@"))
                {
                    Process.Start(new ProcessStartInfo($"mailto:{linkText}") { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo(linkText) { UseShellExecute = true });
                }
            }
            catch { }
        };

        return (lbl, link);
    }

    private Image GetAppIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "favicon.ico");
            if (File.Exists(iconPath))
            {
                int iconSize = Scale(80);
                using var icon = new Icon(iconPath, iconSize, iconSize);
                return icon.ToBitmap();
            }
        }
        catch { }

        // Fallback: Create a modern icon
        int size = Scale(80);
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Draw circle background
            using (var brush = new SolidBrush(PrimaryColor))
            {
                g.FillEllipse(brush, 2, 2, size - 4, size - 4);
            }

            // Draw search icon (scaled)
            float iconScale = size / 80f;
            using (var pen = new Pen(Color.White, 4 * iconScale))
            {
                g.DrawEllipse(pen, 20 * iconScale, 18 * iconScale, 32 * iconScale, 32 * iconScale);
                g.DrawLine(pen, 46 * iconScale, 46 * iconScale, 58 * iconScale, 58 * iconScale);
            }
        }
        return bmp;
    }
}