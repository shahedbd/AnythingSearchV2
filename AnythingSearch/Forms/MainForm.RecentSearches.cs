using AnythingSearch.Models;
using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// Recent-searches panel rendering for MainForm.
/// Split out of MainForm.Search.cs to keep each file focused and small.
/// </summary>
public partial class MainForm
{
    #region Recent Searches

    private void LoadRecentSearches()
    {
        // Reset search tracking when showing recent searches
        _lastSavedSearch = "";
        if (IsDisposed || Disposing) return;

        // Get DPI scale factor
        float dpiScale = this.DeviceDpi / 96f;

        flpRecentSearches.SuspendLayout();
        flpRecentSearches.Controls.Clear();

        var recentSearches = _recentSearchService.GetRecentSearches()
            .Where(s => !s.Query.Contains("Please wait") &&
                       !s.Query.Contains("Building search") &&
                       !s.Query.Contains("Rebuilding") &&
                       !s.Query.StartsWith("⏳") &&
                       s.Query != "Search files and folders...")
            .ToList();

        if (recentSearches.Count == 0)
        {
            var emptyPanel = new Panel
            {
                Size = new Size(flpRecentSearches.Width - (int)(30 * dpiScale), (int)(100 * dpiScale)),
                BackColor = Color.Transparent
            };

            var statusText = _searchManager.IsDatabaseReady
                ? "No recent searches\n\nStart typing to search files instantly."
                : "No recent searches\n\nBuilding local index in background...";

            var emptyLabel = new Label
            {
                Text = statusText,
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(140, 140, 140),
                AutoSize = false,
                Size = new Size(emptyPanel.Width, (int)(80 * dpiScale)),
                Location = new Point((int)(10 * dpiScale), (int)(15 * dpiScale)),
                TextAlign = ContentAlignment.TopCenter
            };

            emptyPanel.Controls.Add(emptyLabel);
            flpRecentSearches.Controls.Add(emptyPanel);
            flpRecentSearches.ResumeLayout();
            return;
        }

        foreach (var item in recentSearches)
        {
            var panelWidth = flpRecentSearches.ClientSize.Width - (int)(20 * dpiScale);
            var panelHeight = (int)(52 * dpiScale);  // More compact
            var leftPadding = (int)(14 * dpiScale);
            var btnSize = (int)(28 * dpiScale);

            var panel = new Panel
            {
                Size = new Size(panelWidth, panelHeight),
                Margin = new Padding((int)(3 * dpiScale), (int)(2 * dpiScale), (int)(3 * dpiScale), (int)(2 * dpiScale)),
                BackColor = Color.White,
                Cursor = Cursors.Hand
            };

            // Subtle border on paint
            panel.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(230, 230, 230), 1);
                e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
            };

            // Hover effect
            panel.MouseEnter += (s, e) => panel.BackColor = Color.FromArgb(248, 250, 252);
            panel.MouseLeave += (s, e) => panel.BackColor = Color.White;

            // Query label - the search term (compact, professional)
            var lblQuery = new Label
            {
                Text = item.Query,
                Font = new Font("Segoe UI", 10F, FontStyle.Regular),
                ForeColor = Color.FromArgb(0, 102, 204),  // Professional blue
                Location = new Point(leftPadding, (int)(8 * dpiScale)),
                Size = new Size(panelWidth - leftPadding - btnSize - (int)(16 * dpiScale), (int)(20 * dpiScale)),
                AutoSize = false,
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };

            // Info label - results count and time (smaller, gray)
            var lblInfo = new Label
            {
                Text = $"{item.ResultCount:N0} results  •  {GetRelativeTime(item.Timestamp)}",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(130, 130, 130),
                Location = new Point(leftPadding, (int)(28 * dpiScale)),
                Size = new Size(panelWidth - leftPadding - btnSize - (int)(16 * dpiScale), (int)(16 * dpiScale)),
                AutoSize = false,
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };

            // Remove button - subtle X
            var btnRemove = new Button
            {
                Text = "×",
                Size = new Size(btnSize, btnSize),
                Location = new Point(panelWidth - btnSize - (int)(10 * dpiScale), (panelHeight - btnSize) / 2),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(180, 180, 180),
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 12F, FontStyle.Regular),
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnRemove.FlatAppearance.BorderSize = 0;
            btnRemove.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 235, 235);
            btnRemove.FlatAppearance.MouseDownBackColor = Color.FromArgb(255, 220, 220);
            btnRemove.MouseEnter += (s, e) => btnRemove.ForeColor = Color.FromArgb(220, 80, 80);
            btnRemove.MouseLeave += (s, e) => btnRemove.ForeColor = Color.FromArgb(180, 180, 180);
            btnRemove.Click += (s, e) =>
            {
                _recentSearchService.RemoveSearch(item.Query);
                LoadRecentSearches();
            };

            EventHandler clickHandler = (s, e) =>
            {
                txtSearch.Text = item.Query;
                txtSearch.ForeColor = AppColors.TextPrimary;
                txtSearch.Focus();
                txtSearch.SelectionStart = txtSearch.Text.Length;
            };

            panel.Click += clickHandler;
            lblQuery.Click += clickHandler;
            lblInfo.Click += clickHandler;
            lblQuery.MouseEnter += (s, e) => panel.BackColor = Color.FromArgb(248, 250, 252);
            lblQuery.MouseLeave += (s, e) => panel.BackColor = Color.White;
            lblInfo.MouseEnter += (s, e) => panel.BackColor = Color.FromArgb(248, 250, 252);
            lblInfo.MouseLeave += (s, e) => panel.BackColor = Color.White;

            panel.Controls.AddRange(new Control[] { lblQuery, lblInfo, btnRemove });
            flpRecentSearches.Controls.Add(panel);
        }

        flpRecentSearches.ResumeLayout();
    }

    private string GetRelativeTime(DateTime timestamp)
    {
        var diff = DateTime.Now - timestamp;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return timestamp.ToString("MMM dd");
    }

    private void LnkClearRecent_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        if (MessageBox.Show("Clear all recent searches?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _recentSearchService.ClearRecentSearches();
            LoadRecentSearches();
        }
    }

    #endregion
}
