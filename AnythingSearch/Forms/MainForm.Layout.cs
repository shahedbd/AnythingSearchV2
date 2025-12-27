using AnythingSearch.Helper;
using System.Runtime.InteropServices;

namespace AnythingSearch.Forms;

/// <summary>
/// UI Layout and component initialization for MainForm
/// DPI-aware layout for 150% scaling support (Microsoft Store requirement)
/// </summary>
public partial class MainForm
{
    private void InitializeComponent()
    {
        // CRITICAL: DPI Scaling settings for Microsoft Store compliance
        this.AutoScaleMode = AutoScaleMode.Dpi;
        this.AutoScaleDimensions = new SizeF(96F, 96F);

        this.Text = "Anything Search";
        this.Size = new Size(1150, 680);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MinimumSize = new Size(900, 550);
        this.BackColor = AppColors.Background;
        this.FormClosing += MainForm_FormClosing;
        this.Resize += MainForm_Resize;
        this.Icon = CommonHelper.LoadApplicationIcon();

        // Enable double buffering for smoother rendering
        this.DoubleBuffered = true;
        this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        // Use system font for proper DPI scaling
        this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        InitializeMenuStrip();
        InitializeHeader();
        InitializeContentArea();
        InitializeStatusBar();

        // Attach TextChanged event AFTER all controls are created to avoid null reference
        txtSearch.TextChanged += TxtSearch_TextChanged;
    }

    #region Menu Strip

    private void InitializeMenuStrip()
    {
        menuStrip = new MenuStrip
        {
            BackColor = AppColors.Surface,
            Font = new Font("Segoe UI", 9.5F),
            Padding = new Padding(8, 2, 0, 2),
            Dock = DockStyle.Top
        };
        menuStrip.Renderer = new ModernToolStripRenderer();

        // File Menu
        var fileMenu = new ToolStripMenuItem("File");

        var rebuildItem = new ToolStripMenuItem("🔄  Rebuild Index", null, BtnIndex_Click, Keys.Control | Keys.R);
        var settingsItem = new ToolStripMenuItem("⚙️  Settings", null, BtnSettings_Click, Keys.Control | Keys.S);
        var minimizeItem = new ToolStripMenuItem("📥  Minimize to Tray", null, (s, e) => MinimizeToTray(), Keys.Control | Keys.M);
        var exitItem = new ToolStripMenuItem("🚪  Exit", null, (s, e) => { _isExiting = true; Application.Exit(); }, Keys.Alt | Keys.F4);

        fileMenu.DropDownItems.Add(rebuildItem);
        fileMenu.DropDownItems.Add(settingsItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(minimizeItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exitItem);

        // Help Menu
        var helpMenu = new ToolStripMenuItem("Help");

        var aboutItem = new ToolStripMenuItem("ℹ️  About Anything Search", null, (s, e) =>
        {
            using var aboutForm = new AboutForm();
            aboutForm.ShowDialog(this);
        }, Keys.F1);

        var updateItem = new ToolStripMenuItem("🔃  Check for Updates...", null, (s, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://zerobytebd.com/anything-search",
                    UseShellExecute = true
                });
            }
            catch { }
        });

        var websiteItem = new ToolStripMenuItem("🌐  Visit Website", null, (s, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://zerobytebd.com/anything-search",
                    UseShellExecute = true
                });
            }
            catch { }
        });

        helpMenu.DropDownItems.Add(aboutItem);
        helpMenu.DropDownItems.Add(updateItem);
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add(websiteItem);

        menuStrip.Items.AddRange(new ToolStripItem[] { fileMenu, helpMenu });
        this.Controls.Add(menuStrip);
        this.MainMenuStrip = menuStrip;
    }

    #endregion

    #region Header Panel

    private void InitializeHeader()
    {
        int menuHeight = menuStrip.Height;

        // Header Panel with Search
        pnlHeader = new Panel
        {
            Location = new Point(0, menuHeight),
            Size = new Size(this.ClientSize.Width, 70),
            BackColor = AppColors.Surface,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        pnlHeader.Paint += (s, e) =>
        {
            using var pen = new Pen(AppColors.Border, 1);
            e.Graphics.DrawLine(pen, 0, pnlHeader.Height - 1, pnlHeader.Width, pnlHeader.Height - 1);
        };

        // Search Container
        pnlSearchContainer = new Panel
        {
            Location = new Point(20, 15),
            Size = new Size(680, 40),
            BackColor = AppColors.Surface,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.FixedSingle
        };

        // Search TextBox
        txtSearch = new TextBox
        {
            Location = new Point(36, 8),
            Size = new Size(600, 24),
            Font = new Font("Segoe UI", 12F),
            BorderStyle = BorderStyle.None,
            BackColor = AppColors.Surface,
            ForeColor = AppColors.TextPrimary,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        txtSearch.KeyDown += TxtSearch_KeyDown;

        // Search Icon
        var lblSearchIcon = new Label
        {
            Text = "🔍",
            Location = new Point(8, 9),
            Size = new Size(26, 22),
            Font = new Font("Segoe UI", 11F),
            ForeColor = AppColors.TextMuted
        };

        // Clear button
        btnClearSearch = new Button
        {
            Text = "✕",
            Size = new Size(28, 28),
            Location = new Point(pnlSearchContainer.Width - 34, 5),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = AppColors.TextMuted,
            Font = new Font("Segoe UI", 9F),
            Cursor = Cursors.Hand,
            Visible = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Name = "btnClearSearch"
        };
        btnClearSearch.FlatAppearance.BorderSize = 0;
        btnClearSearch.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 230, 230);
        btnClearSearch.Click += (s, e) =>
        {
            txtSearch.Text = "";
            txtSearch.ForeColor = AppColors.TextPrimary;
            txtSearch.Focus();
            btnClearSearch.Visible = false;
        };

        // Set initial placeholder
        txtSearch.Text = "Search files and folders...";
        txtSearch.ForeColor = AppColors.TextMuted;
        txtSearch.GotFocus += (s, e) =>
        {
            if (txtSearch.Text == "Search files and folders...")
            {
                txtSearch.Text = "";
                txtSearch.ForeColor = AppColors.TextPrimary;
            }
        };
        txtSearch.LostFocus += (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                txtSearch.Text = "Search files and folders...";
                txtSearch.ForeColor = AppColors.TextMuted;
            }
        };

        pnlSearchContainer.Controls.AddRange(new Control[] { lblSearchIcon, txtSearch, btnClearSearch });

        // Modern Buttons
        btnIndex = CreateModernButton("⟳ Rebuild Index", new Point(720, 15), new Size(130, 40));
        btnIndex.Click += BtnIndex_Click;
        btnIndex.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        btnSettings = CreateModernButton("⚙ Settings", new Point(860, 15), new Size(110, 40));
        btnSettings.Click += BtnSettings_Click;
        btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        // Modern Checkbox
        chkAutoWatch = new CheckBox
        {
            Location = new Point(985, 22),
            Size = new Size(130, 26),
            Text = "  Auto-Watch",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = AppColors.TextPrimary,
            Checked = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Standard
        };
        chkAutoWatch.CheckedChanged += ChkAutoWatch_CheckedChanged;

        pnlHeader.Controls.AddRange(new Control[] { pnlSearchContainer, btnIndex, btnSettings, chkAutoWatch });
        this.Controls.Add(pnlHeader);
    }

    #endregion

    #region Content Area

    private void InitializeContentArea()
    {
        int menuHeight = menuStrip.Height;
        int contentTop = menuHeight + pnlHeader.Height + 10;
        int statusHeight = 55;
        int contentHeight = this.ClientSize.Height - contentTop - statusHeight - 10;

        InitializeContextMenu();
        InitializeRecentSearchesPanel(contentTop, contentHeight);
        InitializeDataGridView(contentTop, contentHeight);
    }

    private void InitializeRecentSearchesPanel(int contentTop, int contentHeight)
    {
        pnlRecentSearches = new Panel
        {
            Location = new Point(20, contentTop),
            Size = new Size(this.ClientSize.Width - 40, contentHeight),
            BackColor = AppColors.Surface,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Visible = true
        };
        pnlRecentSearches.Paint += Panel_PaintBorder;

        lblRecentTitle = new Label
        {
            Location = new Point(20, 18),
            Size = new Size(300, 28),
            Text = "Recent Searches",
            Font = new Font("Segoe UI Semibold", 14F),
            ForeColor = AppColors.TextPrimary
        };

        lnkClearRecent = new LinkLabel
        {
            Location = new Point(pnlRecentSearches.Width - 100, 22),
            Size = new Size(80, 20),
            Text = "Clear All",
            Font = new Font("Segoe UI", 9.5F),
            LinkColor = AppColors.Primary,
            ActiveLinkColor = AppColors.PrimaryDark,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        lnkClearRecent.LinkClicked += LnkClearRecent_LinkClicked;

        flpRecentSearches = new FlowLayoutPanel
        {
            Location = new Point(15, 55),
            Size = new Size(pnlRecentSearches.Width - 30, contentHeight - 70),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = AppColors.Surface,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Padding = new Padding(5)
        };

        pnlRecentSearches.Controls.AddRange(new Control[] { lblRecentTitle, lnkClearRecent, flpRecentSearches });
        this.Controls.Add(pnlRecentSearches);
    }

    private void InitializeDataGridView(int contentTop, int contentHeight)
    {
        dgvResults = new DataGridView
        {
            Location = new Point(20, contentTop),
            Size = new Size(this.ClientSize.Width - 40, contentHeight),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            RowHeadersVisible = false,
            RowTemplate = { Height = 38 },
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ContextMenuStrip = contextMenu,
            Visible = false,
            BackgroundColor = AppColors.Surface,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(240, 240, 240),
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = AppColors.Surface,
                ForeColor = AppColors.TextPrimary,
                SelectionBackColor = Color.FromArgb(230, 240, 255),
                SelectionForeColor = AppColors.TextPrimary,
                Font = new Font("Segoe UI", 9.5F),
                Padding = new Padding(10, 6, 10, 6)
            },
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(250, 251, 252),
                ForeColor = AppColors.TextPrimary,
                SelectionBackColor = Color.FromArgb(230, 240, 255),
                SelectionForeColor = AppColors.TextPrimary,
                Font = new Font("Segoe UI", 9.5F),
                Padding = new Padding(10, 6, 10, 6)
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(248, 249, 251),
                ForeColor = Color.FromArgb(80, 80, 80),
                Font = new Font("Segoe UI Semibold", 9F),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 10, 0),
                WrapMode = DataGridViewTriState.False
            },
            ColumnHeadersHeight = 42,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            EnableHeadersVisualStyles = false
        };

        // Custom paint for professional header with bottom border
        dgvResults.CellPainting += DgvResults_CellPainting;

        // Border around the entire grid
        dgvResults.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(218, 220, 224), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, dgvResults.Width - 1, dgvResults.Height - 1);
        };

        SetupDataGridColumns();

        dgvResults.DoubleClick += DgvResults_DoubleClick;
        dgvResults.CellMouseDown += DgvResults_CellMouseDown;
        dgvResults.CellFormatting += DgvResults_CellFormatting;
        dgvResults.CellMouseEnter += (s, e) =>
        {
            if (e.RowIndex >= 0) dgvResults.Cursor = Cursors.Hand;
        };
        dgvResults.CellMouseLeave += (s, e) => dgvResults.Cursor = Cursors.Default;

        this.Controls.Add(dgvResults);
    }

    /// <summary>
    /// Custom cell painting for professional header appearance
    /// </summary>
    private void DgvResults_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex == -1) // Header row
        {
            e.PaintBackground(e.ClipBounds, true);

            // Draw header text
            using var brush = new SolidBrush(Color.FromArgb(70, 70, 70));
            using var sf = new StringFormat
            {
                Alignment = e.ColumnIndex == 3 ? StringAlignment.Far : StringAlignment.Near, // Size column right-aligned
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            var textRect = new Rectangle(
                e.CellBounds.X + 12,
                e.CellBounds.Y,
                e.CellBounds.Width - 24,
                e.CellBounds.Height
            );

            if (e.Value != null)
            {
                e.Graphics.DrawString(e.Value.ToString(),
                    new Font("Segoe UI Semibold", 9F),
                    brush,
                    textRect,
                    sf);
            }

            // Draw bottom border line for header
            using var borderPen = new Pen(Color.FromArgb(218, 220, 224), 1);
            e.Graphics.DrawLine(borderPen,
                e.CellBounds.Left,
                e.CellBounds.Bottom - 1,
                e.CellBounds.Right,
                e.CellBounds.Bottom - 1);

            e.Handled = true;
        }
    }

    private void SetupDataGridColumns()
    {
        dgvResults.Columns.Clear();

        var iconColumn = new DataGridViewImageColumn
        {
            Name = "Icon",
            HeaderText = "",
            Width = 40,
            MinimumWidth = 40,
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            Resizable = DataGridViewTriState.False,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        };
        dgvResults.Columns.Add(iconColumn);

        dgvResults.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "Name",
            FillWeight = 30,
            MinimumWidth = 180
        });

        dgvResults.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Path",
            HeaderText = "Location",
            FillWeight = 50,
            MinimumWidth = 250
        });

        dgvResults.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Size",
            HeaderText = "Size",
            Width = 100,
            MinimumWidth = 80,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });

        dgvResults.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Modified",
            HeaderText = "Date Modified",
            Width = 150,
            MinimumWidth = 130,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        });
    }

    #endregion

    #region Status Bar

    private void InitializeStatusBar()
    {
        int statusHeight = 55;

        Panel statusPanel = new Panel
        {
            Location = new Point(0, this.ClientSize.Height - statusHeight),
            Size = new Size(this.ClientSize.Width, statusHeight),
            BackColor = AppColors.StatusBar,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        statusPanel.Paint += (s, e) =>
        {
            using var pen = new Pen(AppColors.Border, 1);
            e.Graphics.DrawLine(pen, 0, 0, statusPanel.Width, 0);
        };

        lblSearchInfo = new Label
        {
            Location = new Point(20, 10),
            Size = new Size(600, 20),
            Text = "Ready",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = AppColors.TextSecondary
        };

        lblTotalFiles = new Label
        {
            Location = new Point(this.ClientSize.Width - 270, 10),
            Size = new Size(250, 20),
            Text = "Total: 0 items indexed",
            Font = new Font("Segoe UI Semibold", 9.5F),
            ForeColor = AppColors.Primary,
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        lblWatchStatus = new Label
        {
            Location = new Point(20, 32),
            Size = new Size(this.ClientSize.Width - 40, 18),
            Text = "Auto-watch: Waiting for index...",
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = AppColors.TextMuted,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        progressBar = new ProgressBar
        {
            Location = new Point(20, statusHeight - 4),
            Size = new Size(this.ClientSize.Width - 40, 4),
            Visible = false,
            Style = ProgressBarStyle.Marquee,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        // Resize grip in bottom-right corner
        var lblResizeGrip = new Label
        {
            Text = "⋱",
            Location = new Point(this.ClientSize.Width - 22, statusHeight - 20),
            Size = new Size(20, 18),
            Font = new Font("Segoe UI", 10F),
            ForeColor = AppColors.TextMuted,
            Cursor = Cursors.SizeNWSE,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleCenter
        };
        lblResizeGrip.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, 0x112, 0xF008, 0);
            }
        };

        statusPanel.Controls.AddRange(new Control[] { lblSearchInfo, lblTotalFiles, lblWatchStatus, progressBar, lblResizeGrip });
        this.Controls.Add(statusPanel);
    }

    #endregion

    #region P/Invoke for Resize Grip

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    #endregion
}