using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// Settings form for configuring Anything Search
/// DPI-aware design for Microsoft Store compliance
/// </summary>
public partial class SettingsForm : Form
{
    private readonly SettingsManager _settingsManager;

    // Colors
    private static readonly Color PrimaryColor = Color.FromArgb(0, 120, 212);
    private static readonly Color BackgroundColor = Color.FromArgb(250, 250, 250);
    private static readonly Color CardColor = Color.White;
    private static readonly Color BorderColor = Color.FromArgb(229, 229, 229);
    private static readonly Color TextPrimary = Color.FromArgb(32, 32, 32);
    private static readonly Color TextSecondary = Color.FromArgb(96, 96, 96);

    // Controls
    private ListBox lstExcludedFolders = null!;
    private Button btnAddFolder = null!;
    private Button btnRemoveFolder = null!;
    private CheckBox chkStartWithWindows = null!;
    private CheckBox chkMinimizeToTray = null!;
    private Button btnSave = null!;
    private Button btnCancel = null!;

    public SettingsForm(SettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        InitializeComponent();
        LoadSettings();
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        // CRITICAL: DPI Scaling for Microsoft Store compliance (150% scaling)
        this.AutoScaleMode = AutoScaleMode.Dpi;
        this.AutoScaleDimensions = new SizeF(96F, 96F);
        this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        this.ClientSize = new Size(500, 500);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.Name = "SettingsForm";
        this.StartPosition = FormStartPosition.CenterParent;
        this.Text = "Settings - Anything Search";
        this.BackColor = BackgroundColor;

        // Title
        var lblTitle = new Label
        {
            Text = "⚙️ Settings",
            Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            ForeColor = TextPrimary,
            Location = new Point(24, 20),
            AutoSize = true
        };

        // Excluded Folders Section
        var lblExcluded = new Label
        {
            Text = "Excluded Folders",
            Font = new Font("Segoe UI Semibold", 11F),
            ForeColor = TextPrimary,
            Location = new Point(24, 70),
            AutoSize = true
        };

        var lblExcludedDesc = new Label
        {
            Text = "Folders in this list will not be indexed",
            Font = new Font("Segoe UI", 9F),
            ForeColor = TextSecondary,
            Location = new Point(24, 92),
            AutoSize = true
        };

        lstExcludedFolders = new ListBox
        {
            Location = new Point(24, 118),
            Size = new Size(350, 300),
            Font = new Font("Segoe UI", 9.5F),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = CardColor
        };

        btnAddFolder = CreateButton("Add Folder", new Point(390, 118), new Size(90, 32));
        btnAddFolder.Click += BtnAddFolder_Click;

        btnRemoveFolder = CreateButton("Remove", new Point(390, 156), new Size(90, 32));
        btnRemoveFolder.Click += BtnRemoveFolder_Click;

        // Options Section
        var lblOptions = new Label
        {
            Text = "Options",
            Font = new Font("Segoe UI Semibold", 11F),
            ForeColor = TextPrimary,
            Location = new Point(24, 320),
            AutoSize = true
        };

        chkStartWithWindows = new CheckBox
        {
            Text = "Start with Windows",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = TextPrimary,
            Location = new Point(24, 350),
            AutoSize = true,
            Cursor = Cursors.Hand
        };

        chkMinimizeToTray = new CheckBox
        {
            Text = "Minimize to system tray when closing",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = TextPrimary,
            Location = new Point(24, 378),
            AutoSize = true,
            Cursor = Cursors.Hand
        };

        // Buttons
        btnSave = CreateButton("Save", new Point(290, 450), new Size(100, 36));
        btnSave.BackColor = PrimaryColor;
        btnSave.ForeColor = Color.White;
        btnSave.Click += BtnSave_Click;

        btnCancel = CreateButton("Cancel", new Point(400, 450), new Size(80, 36));
        btnCancel.Click += (s, e) => Close();

        this.Controls.AddRange(new Control[]
        {
            lblTitle, lblExcluded, lblExcludedDesc, lstExcludedFolders,
            btnAddFolder, btnRemoveFolder,
            //lblOptions, chkStartWithWindows, chkMinimizeToTray,
            btnSave, btnCancel
        });

        ResumeLayout(false);
        PerformLayout();
    }

    private Button CreateButton(string text, Point location, Size size)
    {
        var btn = new Button
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = CardColor,
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 9F),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = BorderColor;
        return btn;
    }

    private void LoadSettings()
    {
        var settings = _settingsManager.Settings;

        lstExcludedFolders.Items.Clear();
        foreach (var folder in settings.ExcludedFolders)
        {
            lstExcludedFolders.Items.Add(folder);
        }

        chkStartWithWindows.Checked = settings.StartWithWindows;
        chkMinimizeToTray.Checked = settings.MinimizeToTray;
    }

    private void BtnAddFolder_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select folder to exclude from indexing",
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            if (!lstExcludedFolders.Items.Contains(dialog.SelectedPath))
            {
                lstExcludedFolders.Items.Add(dialog.SelectedPath);
            }
        }
    }

    private void BtnRemoveFolder_Click(object? sender, EventArgs e)
    {
        if (lstExcludedFolders.SelectedIndex >= 0)
        {
            lstExcludedFolders.Items.RemoveAt(lstExcludedFolders.SelectedIndex);
        }
    }

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        var settings = _settingsManager.Settings;

        settings.ExcludedFolders.Clear();
        foreach (var item in lstExcludedFolders.Items)
        {
            settings.ExcludedFolders.Add(item.ToString()!);
        }

        settings.StartWithWindows = chkStartWithWindows.Checked;
        settings.MinimizeToTray = chkMinimizeToTray.Checked;

        _settingsManager.Save();

        // Handle startup registration
        if (settings.StartWithWindows)
            RegisterStartup();
        else
            UnregisterStartup();

        MessageBox.Show("Settings saved successfully!", "Settings",
            MessageBoxButtons.OK, MessageBoxIcon.Information);

        Close();
    }

    private void RegisterStartup()
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            key?.SetValue("AnythingSearch", Application.ExecutablePath);
        }
        catch { }
    }

    private void UnregisterStartup()
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("AnythingSearch", false);
        }
        catch { }
    }
}