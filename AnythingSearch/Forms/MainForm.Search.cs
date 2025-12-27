using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// Search functionality for MainForm
/// Uses SearchManager to automatically switch between Windows Search and SQLite
/// </summary>
public partial class MainForm
{
    // Track the last saved search to avoid duplicates
    private string _lastSavedSearch = "";
    private DateTime _lastSearchTime = DateTime.MinValue;

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

        // Don't search until at least 2 characters
        if (searchText.Length < 2)
        {
            lblSearchInfo.Text = "Type at least 2 characters to search...";
            return;
        }

        // Cancel any pending search
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var currentToken = _searchCts.Token;

        // Show "searching" indicator immediately
        lblSearchInfo.Text = "Searching...";

        // Debounce: wait for user to stop typing (400ms)
        // This prevents searches on every keystroke
        try { await Task.Delay(400, currentToken); }
        catch (TaskCanceledException) { return; }

        if (currentToken.IsCancellationRequested) return;

        // Now perform the search
        pnlRecentSearches.Visible = false;
        dgvResults.Visible = true;

        await PerformSearchAsync(searchText, currentToken);
    }

    private async Task PerformSearchAsync(string searchText, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Use SearchManager which automatically selects the best search source
        var (results, source) = await _searchManager.SearchAsync(searchText, 1000, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;

        var displayResults = results.Take(1000).ToList();

        dgvResults.SuspendLayout();
        dgvResults.Rows.Clear();

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

        // Only save to recent searches if:
        // 1. At least 3 characters
        // 2. Different from last saved search (not a prefix of it or vice versa)
        // 3. At least 2 seconds since last save (prevents rapid saves while typing)
        if (searchText.Length >= 3)
        {
            var now = DateTime.Now;
            var timeSinceLastSave = (now - _lastSearchTime).TotalSeconds;

            // Check if this is a genuinely new search (not just typing more characters)
            bool isNewSearch = string.IsNullOrEmpty(_lastSavedSearch) ||
                               (!searchText.StartsWith(_lastSavedSearch, StringComparison.OrdinalIgnoreCase) &&
                                !_lastSavedSearch.StartsWith(searchText, StringComparison.OrdinalIgnoreCase));

            // Save if it's a new search OR if user has paused typing for 2+ seconds
            if (isNewSearch || timeSinceLastSave >= 2.0)
            {
                // Update the existing entry if it's a continuation of the same search
                if (!string.IsNullOrEmpty(_lastSavedSearch) &&
                    (searchText.StartsWith(_lastSavedSearch, StringComparison.OrdinalIgnoreCase) ||
                     _lastSavedSearch.StartsWith(searchText, StringComparison.OrdinalIgnoreCase)))
                {
                    // Remove the old partial search
                    _recentSearchService.RemoveSearch(_lastSavedSearch);
                }

                _recentSearchService.AddSearch(searchText, results.Count);
                _lastSavedSearch = searchText;
                _lastSearchTime = now;
            }
        }

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
        {
            e.SuppressKeyPress = true;
            // On Enter, save the current search immediately
            var searchText = txtSearch.Text;
            if (searchText.Length >= 3 && searchText != "Search files and folders...")
            {
                // Remove any partial searches that led to this one
                if (!string.IsNullOrEmpty(_lastSavedSearch) &&
                    searchText.StartsWith(_lastSavedSearch, StringComparison.OrdinalIgnoreCase))
                {
                    _recentSearchService.RemoveSearch(_lastSavedSearch);
                }
                _recentSearchService.AddSearch(searchText, dgvResults.Rows.Count);
                _lastSavedSearch = searchText;
                _lastSearchTime = DateTime.Now;
            }
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            // Reset tracking when user escapes
            _lastSavedSearch = "";
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
                Size = new Size(flpRecentSearches.Width - (int)(30 * dpiScale), (int)(120 * dpiScale)),
                BackColor = Color.Transparent
            };

            var statusText = _searchManager.IsDatabaseReady
                ? "No recent searches\n\nStart typing to search files instantly."
                : "No recent searches\n\nStart typing to search (building local index in background).";

            var emptyLabel = new Label
            {
                Text = statusText,
                Font = new Font("Segoe UI", 11F),
                ForeColor = AppColors.TextMuted,
                AutoSize = false,
                Size = new Size(emptyPanel.Width, (int)(100 * dpiScale)),
                Location = new Point((int)(10 * dpiScale), (int)(20 * dpiScale)),
                TextAlign = ContentAlignment.TopCenter
            };

            emptyPanel.Controls.Add(emptyLabel);
            flpRecentSearches.Controls.Add(emptyPanel);
            flpRecentSearches.ResumeLayout();
            return;
        }

        foreach (var item in recentSearches)
        {
            var panelWidth = flpRecentSearches.ClientSize.Width - (int)(25 * dpiScale);
            var panelHeight = (int)(65 * dpiScale);
            var padding = (int)(16 * dpiScale);
            var btnSize = (int)(32 * dpiScale);

            var panel = new Panel
            {
                Size = new Size(panelWidth, panelHeight),
                Margin = new Padding((int)(5 * dpiScale), (int)(4 * dpiScale), (int)(5 * dpiScale), (int)(4 * dpiScale)),
                BackColor = AppColors.Surface,
                Cursor = Cursors.Hand
            };
            panel.Paint += Panel_PaintBorder;
            panel.MouseEnter += (s, e) => panel.BackColor = AppColors.Selected;
            panel.MouseLeave += (s, e) => panel.BackColor = AppColors.Surface;

            // Query label - the search term
            var lblQuery = new Label
            {
                Text = item.Query,
                Font = new Font("Segoe UI Semibold", 11F),
                ForeColor = AppColors.Primary,
                Location = new Point(padding, (int)(10 * dpiScale)),
                Size = new Size(panelWidth - padding - btnSize - (int)(20 * dpiScale), (int)(22 * dpiScale)),
                AutoSize = false,
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };

            // Info label - results count and time (single line, no wrap)
            var lblInfo = new Label
            {
                Text = $"{item.ResultCount:N0} results  •  {GetRelativeTime(item.Timestamp)}",
                Font = new Font("Segoe UI", 9F),
                ForeColor = AppColors.TextMuted,
                Location = new Point(padding, (int)(34 * dpiScale)),
                Size = new Size(panelWidth - padding - btnSize - (int)(20 * dpiScale), (int)(18 * dpiScale)),
                AutoSize = false,
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };

            var btnRemove = new Button
            {
                Text = "✕",
                Size = new Size(btnSize, btnSize),
                Location = new Point(panelWidth - btnSize - (int)(12 * dpiScale), (panelHeight - btnSize) / 2),
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