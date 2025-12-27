using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// Search functionality for MainForm
/// Uses SearchManager to automatically switch between Windows Search and SQLite
/// </summary>
public partial class MainForm
{
    #region Search Event Handlers

    private async void TxtSearch_TextChanged(object? sender, EventArgs e)
    {
        // Guard against calls during initialization
        if (btnClearSearch == null || dgvResults == null || pnlRecentSearches == null || lblSearchInfo == null)
            return;

        var placeholderText = "Search files and folders...";
        var searchText = txtSearch.Text;

        // Handle placeholder
        if (searchText == placeholderText || string.IsNullOrWhiteSpace(searchText))
        {
            btnClearSearch.Visible = false;
            dgvResults.Visible = false;
            pnlRecentSearches.Visible = true;
            LoadRecentSearches();
            lblSearchInfo.Text = _searchManager.IsDatabaseReady ? "Ready" : "Ready (using Windows Search)";
            return;
        }

        btnClearSearch.Visible = true;
        pnlRecentSearches.Visible = false;
        dgvResults.Visible = true;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var currentToken = _searchCts.Token;

        try { await Task.Delay(200, currentToken); }
        catch (TaskCanceledException) { return; }

        if (currentToken.IsCancellationRequested) return;

        await PerformSearchAsync(searchText, currentToken);
    }

    private async Task PerformSearchAsync(string searchText, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Use SearchManager which automatically selects the best search source
        var (results, source) = await _searchManager.SearchAsync(searchText, 1000, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;

        var displayResults = results.Take(1000).ToList();

        dgvResults.Rows.Clear();
        dgvResults.SuspendLayout();

        try
        {
            foreach (var item in displayResults)
            {
                dgvResults.Rows.Add(
                    GetCachedIcon(item.Path, item.IsFolder),
                    item.Name,
                    item.Path,
                    item.IsFolder ? "" : FormatSize(item.Size),
                    item.Modified.ToString("yyyy-MM-dd  HH:mm")
                );
            }
        }
        finally { dgvResults.ResumeLayout(); }

        sw.Stop();

        if (searchText.Length >= 3)
            _recentSearchService.AddSearch(searchText, results.Count);

        // Show search source in results
        var sourceText = source switch
        {
            SearchSource.SQLite => "Local DB",
            SearchSource.WindowsSearch => "Windows Search",
            _ => "Search"
        };

        lblSearchInfo.Text = results.Count > 1000
            ? $"Found {results.Count:N0} results (showing 1,000)  •  {sw.ElapsedMilliseconds}ms  •  {sourceText}"
            : $"Found {results.Count:N0} results  •  {sw.ElapsedMilliseconds}ms  •  {sourceText}";
    }

    private void TxtSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
            e.SuppressKeyPress = true;
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            MinimizeToTray();
        }
        else if (e.KeyCode == Keys.Down && dgvResults.Visible && dgvResults.Rows.Count > 0)
        {
            // Navigate to results with arrow key
            e.SuppressKeyPress = true;
            dgvResults.Focus();
            if (dgvResults.Rows.Count > 0)
                dgvResults.Rows[0].Selected = true;
        }
    }

    #endregion

    #region Recent Searches

    private void LoadRecentSearches()
    {
        if (IsDisposed || Disposing) return;

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
                Size = new Size(flpRecentSearches.Width - 30, 120),
                BackColor = Color.Transparent
            };

            var statusText = _searchManager.IsDatabaseReady
                ? "No recent searches\n\nStart typing to search files instantly.\nSearches with 3+ characters will appear here."
                : "No recent searches\n\nStart typing to search (building local index in background).\nSearches with 3+ characters will appear here.";

            var emptyLabel = new Label
            {
                Text = statusText,
                Font = new Font("Segoe UI", 11F),
                ForeColor = AppColors.TextMuted,
                AutoSize = false,
                Size = new Size(emptyPanel.Width, 100),
                Location = new Point(10, 20),
                TextAlign = ContentAlignment.TopCenter
            };

            emptyPanel.Controls.Add(emptyLabel);
            flpRecentSearches.Controls.Add(emptyPanel);
            flpRecentSearches.ResumeLayout();
            return;
        }

        foreach (var item in recentSearches)
        {
            var panelWidth = flpRecentSearches.ClientSize.Width - 25;

            var panel = new Panel
            {
                Size = new Size(panelWidth, 60),
                Margin = new Padding(5, 4, 5, 4),
                BackColor = AppColors.Surface,
                Cursor = Cursors.Hand
            };
            panel.Paint += Panel_PaintBorder;
            panel.MouseEnter += (s, e) => panel.BackColor = AppColors.Selected;
            panel.MouseLeave += (s, e) => panel.BackColor = AppColors.Surface;

            var lblQuery = new Label
            {
                Text = item.Query,
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = AppColors.Primary,
                Location = new Point(16, 12),
                AutoSize = true,
                Cursor = Cursors.Hand
            };

            var lblInfo = new Label
            {
                Text = $"{item.ResultCount:N0} results  •  {GetRelativeTime(item.Timestamp)}",
                Font = new Font("Segoe UI", 9F),
                ForeColor = AppColors.TextMuted,
                Location = new Point(16, 34),
                AutoSize = true,
                Cursor = Cursors.Hand
            };

            var btnRemove = new Button
            {
                Text = "✕",
                Size = new Size(32, 32),
                Location = new Point(panelWidth - 45, 14),
                FlatStyle = FlatStyle.Flat,
                ForeColor = AppColors.TextMuted,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 10F),
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnRemove.FlatAppearance.BorderSize = 0;
            btnRemove.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 220, 220);
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
            lblQuery.MouseEnter += (s, e) => panel.BackColor = AppColors.Selected;
            lblQuery.MouseLeave += (s, e) => panel.BackColor = AppColors.Surface;
            lblInfo.MouseEnter += (s, e) => panel.BackColor = AppColors.Selected;
            lblInfo.MouseLeave += (s, e) => panel.BackColor = AppColors.Surface;

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