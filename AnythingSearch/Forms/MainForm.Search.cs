using AnythingSearch.Models;
using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// Search functionality for MainForm
/// Uses SearchManager to automatically switch between Windows Search and SQLite
///
/// Threading rule: the UI thread must never block. The query itself runs on a
/// thread-pool thread (see SearchManager.Query.cs) and only row population happens
/// here, so typing is never interrupted.
/// See MainForm.RecentSearches.cs for the recent-searches panel.
/// </summary>
public partial class MainForm
{
    private const string SearchPlaceholder = "Search files and folders...";
    private const int SearchDebounceMs = 400;
    private const int MaxDisplayedResults = 1000;

    // Track the last saved search to avoid duplicates
    private string _lastSavedSearch = "";
    private DateTime _lastSearchTime = DateTime.MinValue;

    #region Search Event Handlers

    private async void TxtSearch_TextChanged(object? sender, EventArgs e)
    {
        // Guard against calls during initialization
        if (btnClearSearch == null || dgvResults == null || pnlRecentSearches == null || lblSearchInfo == null)
            return;

        var searchText = txtSearch.Text;

        // Handle placeholder
        if (searchText == SearchPlaceholder || string.IsNullOrWhiteSpace(searchText))
        {
            CancelPendingSearch();
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
            CancelPendingSearch();
            lblSearchInfo.Text = "Type at least 2 characters to search...";
            return;
        }

        // Cancel the pending/in-flight search and start a fresh one
        var currentToken = CancelPendingSearch();

        // Show "searching" indicator immediately
        lblSearchInfo.Text = "Searching...";

        try
        {
            // Debounce: only the last keystroke within the window actually searches
            await Task.Delay(SearchDebounceMs, currentToken);

            await PerformSearchAsync(searchText, currentToken);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke - nothing to do
        }
    }

    /// <summary>
    /// Cancels the previous search (if any) and returns the token for the new one.
    /// </summary>
    private CancellationToken CancelPendingSearch()
    {
        var previous = _searchCts;
        _searchCts = new CancellationTokenSource();

        if (previous != null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
            previous.Dispose();
        }

        return _searchCts.Token;
    }

    private async Task PerformSearchAsync(string searchText, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Use SearchManager which automatically selects the best search source.
        // The query runs on a thread-pool thread, so the UI stays responsive while it executes.
        var (results, source) = await _searchManager.SearchAsync(searchText, MaxDisplayedResults, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;

        var displayResults = results.Count > MaxDisplayedResults
            ? results.Take(MaxDisplayedResults).ToList()
            : results;

        // Resolve file-type icons off the UI thread - Icon.ExtractAssociatedIcon hits the disk
        await PrewarmIconCacheAsync(displayResults, cancellationToken);

        if (cancellationToken.IsCancellationRequested || IsDisposed || Disposing) return;

        pnlRecentSearches.Visible = false;
        PopulateResultGrid(displayResults);
        dgvResults.Visible = true;

        sw.Stop();

        SaveToRecentSearches(searchText, results.Count);

        // Show search source in results
        var sourceText = source switch
        {
            SearchSource.SQLite => "Local DB",
            SearchSource.WindowsSearch => "Windows Search",
            _ => "Search"
        };

        lblSearchInfo.Text = results.Count > MaxDisplayedResults
            ? $"Found {results.Count:N0} results (showing {MaxDisplayedResults:N0})  •  {sw.ElapsedMilliseconds}ms  •  {sourceText}"
            : $"Found {results.Count:N0} results  •  {sw.ElapsedMilliseconds}ms  •  {sourceText}";
    }

    /// <summary>
    /// Fill the grid with a single AddRange - adding rows one by one re-runs the
    /// Fill column layout for every row, which is what made large result sets stutter.
    /// </summary>
    private void PopulateResultGrid(List<FileEntry> displayResults)
    {
        var rows = new DataGridViewRow[displayResults.Count];

        for (int i = 0; i < displayResults.Count; i++)
        {
            var item = displayResults[i];
            var row = (DataGridViewRow)dgvResults.RowTemplate.Clone();
            row.CreateCells(
                dgvResults,
                GetCachedIcon(item.Path, item.IsFolder),
                item.Name,
                item.Path,
                item.IsFolder ? "" : FormatSize(item.Size),
                item.Modified.ToString("yyyy-MM-dd  HH:mm"));
            rows[i] = row;
        }

        dgvResults.SuspendLayout();
        try
        {
            dgvResults.Rows.Clear();
            if (rows.Length > 0)
                dgvResults.Rows.AddRange(rows);
        }
        finally { dgvResults.ResumeLayout(); }
    }

    /// <summary>
    /// Save to recent searches only if:
    /// 1. At least 3 characters
    /// 2. Different from last saved search (not a prefix of it or vice versa)
    /// 3. At least 2 seconds since last save (prevents rapid saves while typing)
    /// </summary>
    private void SaveToRecentSearches(string searchText, int resultCount)
    {
        if (searchText.Length < 3) return;

        var now = DateTime.Now;
        var timeSinceLastSave = (now - _lastSearchTime).TotalSeconds;

        // Check if this is a genuinely new search (not just typing more characters)
        bool isNewSearch = string.IsNullOrEmpty(_lastSavedSearch) ||
                           (!searchText.StartsWith(_lastSavedSearch, StringComparison.OrdinalIgnoreCase) &&
                            !_lastSavedSearch.StartsWith(searchText, StringComparison.OrdinalIgnoreCase));

        // Save if it's a new search OR if user has paused typing for 2+ seconds
        if (!isNewSearch && timeSinceLastSave < 2.0) return;

        // Update the existing entry if it's a continuation of the same search
        if (!string.IsNullOrEmpty(_lastSavedSearch) &&
            (searchText.StartsWith(_lastSavedSearch, StringComparison.OrdinalIgnoreCase) ||
             _lastSavedSearch.StartsWith(searchText, StringComparison.OrdinalIgnoreCase)))
        {
            // Remove the old partial search
            _recentSearchService.RemoveSearch(_lastSavedSearch);
        }

        _recentSearchService.AddSearch(searchText, resultCount);
        _lastSavedSearch = searchText;
        _lastSearchTime = now;
    }

    private void TxtSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            // On Enter, save the current search immediately
            var searchText = txtSearch.Text;
            if (searchText.Length >= 3 && searchText != SearchPlaceholder)
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
}
