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

        // Get DPI scale factor for initial size calculation
        float dpiScale = this.DeviceDpi / 96f;

        // Base size at 100% DPI, scales up for higher DPI
        int baseWidth = 1200;
        int baseHeight = 750;
        int minWidth = 950;
        int minHeight = 600;

        this.Text = "Anything Search";
        this.Size = new Size((int)(baseWidth * dpiScale), (int)(baseHeight * dpiScale));
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MinimumSize = new Size((int)(minWidth * dpiScale), (int)(minHeight * dpiScale));
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

        // Get DPI scale factor
        float dpiScale = this.DeviceDpi / 96f;

        // Scale dimensions based on DPI
        int headerHeight = (int)(70 * dpiScale);
        int controlHeight = (int)(40 * dpiScale);
        int padding = (int)(15 * dpiScale);
        int spacing = (int)(10 * dpiScale);

        // Header Panel with Search
        pnlHeader = new Panel
        {
            Location = new Point(0, menuHeight),
            Size = new Size(this.ClientSize.Width, headerHeight),
            BackColor = AppColors.Surface,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        pnlHeader.Paint += (s, e) =>
        {
            using var pen = new Pen(AppColors.Border, 1);
            e.Graphics.DrawLine(pen, 0, pnlHeader.Height - 1, pnlHeader.Width, pnlHeader.Height - 1);
        };

        // Calculate button widths based on DPI
        int rebuildBtnWidth = (int)(130 * dpiScale);
        int settingsBtnWidth = (int)(110 * dpiScale);
        int checkboxWidth = (int)(130 * dpiScale);

        // Calculate right-side controls total width
        int rightControlsWidth = rebuildBtnWidth + spacing + settingsBtnWidth + spacing + checkboxWidth + padding;

        // Search Container - takes remaining space
        int searchWidth = this.ClientSize.Width - rightControlsWidth - padding - spacing;

        pnlSearchContainer = new Panel
        {
            Location = new Point(padding, padding),
            Size = new Size(Math.Max(200, searchWidth), controlHeight),
            BackColor = AppColors.Surface,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.FixedSingle
        };

        // Search TextBox
        int searchIconWidth = (int)(36 * dpiScale);
        int clearBtnWidth = (int)(34 * dpiScale);

        txtSearch = new TextBox
        {
            Location = new Point(searchIconWidth, (int)(8 * dpiScale)),
            Size = new Size(pnlSearchContainer.Width - searchIconWidth - clearBtnWidth, (int)(24 * dpiScale)),
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
            Location = new Point((int)(8 * dpiScale), (int)(9 * dpiScale)),
            Size = new Size((int)(26 * dpiScale), (int)(22 * dpiScale)),
            Font = new Font("Segoe UI", 11F),
            ForeColor = AppColors.TextMuted
        };

        // Clear button
        btnClearSearch = new Button
        {
            Text = "✕",
            Size = new Size((int)(28 * dpiScale), (int)(28 * dpiScale)),
            Location = new Point(pnlSearchContainer.Width - clearBtnWidth, (int)(5 * dpiScale)),
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

        // Calculate button positions from right edge
        int rightEdge = this.ClientSize.Width - padding;

        // Auto-Watch checkbox (rightmost)
        chkAutoWatch = new CheckBox
        {
            Location = new Point(rightEdge - checkboxWidth, padding + (int)(7 * dpiScale)),
            Size = new Size(checkboxWidth, (int)(26 * dpiScale)),
            Text = "Auto-Watch",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = AppColors.TextPrimary,
            Checked = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Standard
        };
        chkAutoWatch.CheckedChanged += ChkAutoWatch_CheckedChanged;

        // Settings button
        int settingsBtnX = rightEdge - checkboxWidth - spacing - settingsBtnWidth;
        btnSettings = CreateModernButton("⚙ Settings", new Point(settingsBtnX, padding), new Size(settingsBtnWidth, controlHeight));
        btnSettings.Click += BtnSettings_Click;
        btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        // Rebuild Index button
        int rebuildBtnX = settingsBtnX - spacing - rebuildBtnWidth;
        btnIndex = CreateModernButton("⟳ Rebuild Index", new Point(rebuildBtnX, padding), new Size(rebuildBtnWidth, controlHeight));
        btnIndex.Click += BtnIndex_Click;
        btnIndex.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        pnlHeader.Controls.AddRange(new Control[] { pnlSearchContainer, btnIndex, btnSettings, chkAutoWatch });
        this.Controls.Add(pnlHeader);
    }

    #endregion

    #region Content Area

    private void InitializeContentArea()
    {
        // Get DPI scale factor
        float dpiScale = this.DeviceDpi / 96f;

        int menuHeight = menuStrip.Height;
        int contentTop = menuHeight + pnlHeader.Height + (int)(10 * dpiScale);
        int statusHeight = (int)(55 * dpiScale);
        int contentHeight = this.ClientSize.Height - contentTop - statusHeight - (int)(10 * dpiScale);
        int padding = (int)(20 * dpiScale);

        InitializeContextMenu();
        InitializeRecentSearchesPanel(contentTop, contentHeight, dpiScale, padding);
        InitializeDataGridView(contentTop, contentHeight, dpiScale, padding);
    }

    private void InitializeRecentSearchesPanel(int contentTop, int contentHeight, float dpiScale, int padding)
    {
        pnlRecentSearches = new Panel
        {
            Location = new Point(padding, contentTop),
            Size = new Size(this.ClientSize.Width - padding * 2, contentHeight),
            BackColor = Color.White,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Visible = true
        };

        // Subtle border
        pnlRecentSearches.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(225, 225, 225), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, pnlRecentSearches.Width - 1, pnlRecentSearches.Height - 1);
        };

        // Header title - clean, professional
        lblRecentTitle = new Label
        {
            Location = new Point((int)(18 * dpiScale), (int)(14 * dpiScale)),
            Size = new Size((int)(200 * dpiScale), (int)(24 * dpiScale)),
            Text = "Recent Searches",
            Font = new Font("Segoe UI", 12F, FontStyle.Regular),
            ForeColor = Color.FromArgb(50, 50, 50)
        };

        // Clear All link - subtle
        lnkClearRecent = new LinkLabel
        {
            Location = new Point(pnlRecentSearches.Width - (int)(80 * dpiScale), (int)(16 * dpiScale)),
            Size = new Size((int)(65 * dpiScale), (int)(20 * dpiScale)),
            Text = "Clear All",
            Font = new Font("Segoe UI", 9F),
            LinkColor = Color.FromArgb(0, 102, 204),
            ActiveLinkColor = Color.FromArgb(0, 80, 160),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        lnkClearRecent.LinkClicked += LnkClearRecent_LinkClicked;

        // Flow panel for search items
        int flowPanelTop = (int)(48 * dpiScale);
        flpRecentSearches = new FlowLayoutPanel
        {
            Location = new Point((int)(10 * dpiScale), flowPanelTop),
            Size = new Size(pnlRecentSearches.Width - (int)(20 * dpiScale), contentHeight - flowPanelTop - (int)(10 * dpiScale)),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.White,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Padding = new Padding((int)(2 * dpiScale))
        };

        pnlRecentSearches.Controls.AddRange(new Control[] { lblRecentTitle, lnkClearRecent, flpRecentSearches });
        this.Controls.Add(pnlRecentSearches);
    }

    private void InitializeDataGridView(int contentTop, int contentHeight, float dpiScale, int padding)
    {
        // Everything-like compact row height
        int rowHeight = (int)(22 * dpiScale);  // Compact like Everything
        int headerHeight = (int)(24 * dpiScale);
        int iconPadding = (int)(2 * dpiScale);

        dgvResults = new DataGridView
        {
            Location = new Point(padding, contentTop),
            Size = new Size(this.ClientSize.Width - padding * 2, contentHeight),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,  // Allow multi-select like Everything
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            RowHeadersVisible = false,
            RowTemplate = { Height = rowHeight },
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ContextMenuStrip = contextMenu,
            Visible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            CellBorderStyle = DataGridViewCellBorderStyle.None,  // No cell borders like Everything
            GridColor = Color.White,

            // Default cell style - compact and clean
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.White,
                ForeColor = Color.Black,
                SelectionBackColor = Color.FromArgb(0, 120, 215),  // Windows blue selection
                SelectionForeColor = Color.White,
                Font = new Font("Segoe UI", 9F),
                Padding = new Padding(iconPadding, 0, iconPadding, 0)
            },

            // Alternating rows - subtle difference
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(252, 252, 252),  // Very subtle alternating
                ForeColor = Color.Black,
                SelectionBackColor = Color.FromArgb(0, 120, 215),
                SelectionForeColor = Color.White,
                Font = new Font("Segoe UI", 9F),
                Padding = new Padding(iconPadding, 0, iconPadding, 0)
            },

            // Header style - Everything-like
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(240, 240, 240),  // Light gray header
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 9F),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding((int)(4 * dpiScale), 0, (int)(4 * dpiScale), 0),
                WrapMode = DataGridViewTriState.False
            },
            ColumnHeadersHeight = headerHeight,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            EnableHeadersVisualStyles = false
        };

        // Simple border paint
        dgvResults.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(200, 200, 200), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, dgvResults.Width - 1, dgvResults.Height - 1);
        };

        SetupDataGridColumns(dpiScale);

        dgvResults.DoubleClick += DgvResults_DoubleClick;
        dgvResults.CellMouseDown += DgvResults_CellMouseDown;
        dgvResults.CellFormatting += DgvResults_CellFormatting;
        dgvResults.KeyDown += DgvResults_KeyDown;

        this.Controls.Add(dgvResults);
    }
    // <summary>
    // Handle keyboard navigation in results grid
    // </summary>
    private void DgvResults_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && dgvResults.SelectedRows.Count > 0)
        {
            e.Handled = true;
            // Open the selected item
            var row = dgvResults.SelectedRows[0];
            var path = row.Cells["Path"].Value?.ToString();
            if (!string.IsNullOrEmpty(path))
            {
                OpenFile(path);
            }
        }
        else if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
        {
            // Return focus to search box
            e.Handled = true;
            txtSearch.Focus();
            txtSearch.SelectionStart = txtSearch.Text.Length;
        }
    }
    private void OpenFile(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", path);
            }
            else if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    private void SetupDataGridColumns(float dpiScale = 1.0f)
    {
        dgvResults.Columns.Clear();

        // Icon column - small like Everything (16x16 icons)
        var iconColumn = new DataGridViewImageColumn
        {
            Name = "Icon",
            HeaderText = "",
            Width = (int)(24 * dpiScale),
            MinimumWidth = (int)(24 * dpiScale),
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            Resizable = DataGridViewTriState.False,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        };
        dgvResults.Columns.Add(iconColumn);

        // Name column
        dgvResults.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "Name",
            FillWeight = 30,
            MinimumWidth = (int)(150 * dpiScale)
        });

        // Path column (Location in Everything)
        dgvResults.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Path",
            HeaderText = "Path",
            FillWeight = 50,
            MinimumWidth = (int)(200 * dpiScale)
        });

        // Size column - right aligned
        dgvResults.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Size",
            HeaderText = "Size",
            Width = (int)(80 * dpiScale),
            MinimumWidth = (int)(60 * dpiScale),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, (int)(8 * dpiScale), 0)
            }
        });

        // Date Modified column
        dgvResults.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Modified",
            HeaderText = "Date Modified",
            Width = (int)(140 * dpiScale),
            MinimumWidth = (int)(100 * dpiScale),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        });
    }

    #endregion

    #region Status Bar

    private void InitializeStatusBar()
    {
        // Get DPI scale factor
        float dpiScale = this.DeviceDpi / 96f;

        int statusHeight = (int)(55 * dpiScale);
        int padding = (int)(20 * dpiScale);
        int topLabelY = (int)(10 * dpiScale);
        int bottomLabelY = (int)(32 * dpiScale);

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
            Location = new Point(padding, topLabelY),
            Size = new Size((int)(this.ClientSize.Width * 0.5), (int)(20 * dpiScale)),
            Text = "Ready",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = AppColors.TextSecondary,
            AutoSize = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        lblTotalFiles = new Label
        {
            Location = new Point(this.ClientSize.Width - (int)(270 * dpiScale), topLabelY),
            Size = new Size((int)(250 * dpiScale), (int)(20 * dpiScale)),
            Text = "Total: 0 items indexed",
            Font = new Font("Segoe UI Semibold", 9.5F),
            ForeColor = AppColors.Primary,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        lblWatchStatus = new Label
        {
            Location = new Point(padding, bottomLabelY),
            Size = new Size(this.ClientSize.Width - padding * 2, (int)(18 * dpiScale)),
            Text = "Auto-watch: Waiting for index...",
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = AppColors.TextMuted,
            AutoSize = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        progressBar = new ProgressBar
        {
            Location = new Point(padding, statusHeight - (int)(4 * dpiScale)),
            Size = new Size(this.ClientSize.Width - padding * 2, (int)(4 * dpiScale)),
            Visible = false,
            Style = ProgressBarStyle.Marquee,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        // Resize grip in bottom-right corner
        var lblResizeGrip = new Label
        {
            Text = "⋱",
            Location = new Point(this.ClientSize.Width - (int)(22 * dpiScale), statusHeight - (int)(20 * dpiScale)),
            Size = new Size((int)(20 * dpiScale), (int)(18 * dpiScale)),
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