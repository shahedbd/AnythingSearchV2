using AnythingSearch.Models;
using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// Indexing functionality for MainForm
/// Uses SearchManager which automatically builds and switches to SQLite database
/// </summary>
public partial class MainForm
{
    #region Index Button Handler

    private async void BtnIndex_Click(object? sender, EventArgs e)
    {
        if (_searchManager.IsIndexing)
        {
            // Offer to cancel ongoing indexing
            var cancelResult = MessageBox.Show(
                "Indexing is in progress.\n\n" +
                "Would you like to cancel it?",
                "Indexing In Progress",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (cancelResult == DialogResult.Yes)
            {
                _searchManager.CancelIndexing();
                lblSearchInfo.Text = "Indexing cancelled";
                UpdateSearchSourceUI();
            }
            return;
        }

        if (_searchManager.IsDatabaseReady)
        {
            // Database is ready - offer to rebuild
            var result = MessageBox.Show(
                "Rebuild Search Index?\n\n" +
                $"Current index: {_searchManager.IndexingStatus.TotalItems:N0} items\n" +
                $"Last updated: {_searchManager.IndexingStatus.IndexingCompletedAt:g}\n\n" +
                "This will scan all drives and rebuild the file index.\n" +
                "The process runs in the background.\n\n" +
                "• All drives indexed in parallel\n" +
                "• System folders excluded\n" +
                "• File monitoring resumes after",
                "Rebuild Index",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            // Stop file watcher during rebuild
            StopFileWatcher();

            // Start rebuild
            SetIndexingUIState(true);
            await _searchManager.RebuildIndexAsync();
        }
        else
        {
            // First time indexing or failed - show info and start
            var result = MessageBox.Show(
                "Build Local Search Index?\n\n" +
                "This will scan all drives and build a local file index for faster searching.\n\n" +
                "Benefits:\n" +
                "• Faster search results than Windows Search\n" +
                "• Works even when Windows Search is disabled\n" +
                "• Searches continue to work using Windows Search during build\n\n" +
                "The process runs in the background and may take several minutes.",
                "Build Index",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (result != DialogResult.Yes) return;

            SetIndexingUIState(true);
            await _searchManager.RebuildIndexAsync();
        }
    }

    #endregion

    #region Indexing UI State

    private void SetIndexingUIState(bool isIndexing)
    {
        SafeInvoke(() =>
        {
            btnIndex.Enabled = !isIndexing;
            btnSettings.Enabled = !isIndexing;
            progressBar.Visible = isIndexing;

            if (isIndexing)
            {
                btnIndex.Text = "⏳ Indexing...";
                lblWatchStatus.Text = "Building local database...";
                lblWatchStatus.ForeColor = AppColors.Warning;

                // Keep search box enabled - can still search using Windows Search
                txtSearch.Enabled = true;
            }
            else
            {
                UpdateSearchSourceUI();
            }
        });
    }

    #endregion

    #region Indexing Events

    private void OnIndexingProgress(IndexProgress progress)
    {
        SafeInvoke(() =>
        {
            lblSearchInfo.Text = $"📂 {progress.TotalFolders:N0} folders  •  📄 {progress.TotalFiles:N0} files  •  ⚡ {progress.ItemsPerSecond:N0}/sec";
            lblWatchStatus.Text = $"Indexing: {TruncatePath(progress.CurrentPath, 80)}";
            lblWatchStatus.ForeColor = AppColors.Warning;

            if ((progress.TotalFiles + progress.TotalFolders) % 50000 == 0)
                UpdateTrayStatus($"Indexing: {progress.TotalFiles + progress.TotalFolders:N0} items...");

            if ((progress.TotalFiles + progress.TotalFolders) % 10000 == 0)
                _ = UpdateTotalCountAsync();
        });
    }

    private void OnIndexingCompleted()
    {
        SafeInvoke(async () =>
        {
            SetIndexingUIState(false);
            await UpdateTotalCountAsync();

            var status = _searchManager.IndexingStatus;
            UpdateTrayStatus($"{status.TotalItems:N0} items indexed");

            // Show completion in status bar instead of popup
            lblSearchInfo.Text = $"✓ Index complete! {status.TotalItems:N0} items";
            lblWatchStatus.Text = $"✓ Local database ready ({status.TotalItems:N0} items)";
            lblWatchStatus.ForeColor = AppColors.Success;

            if (chkAutoWatch.Checked)
                StartFileWatcher();
        });
    }

    #endregion

    #region File Watcher

    private void StartFileWatcher()
    {
        if (_searchManager.IsIndexing)
        {
            SafeInvoke(() =>
            {
                lblWatchStatus.Text = "Auto-watch: Will start after indexing...";
                lblWatchStatus.ForeColor = AppColors.Warning;
            });
            return;
        }

        if (!_searchManager.IsDatabaseReady)
        {
            SafeInvoke(() =>
            {
                lblWatchStatus.Text = "Auto-watch: Waiting for database...";
                lblWatchStatus.ForeColor = AppColors.TextMuted;
            });
            return;
        }

        _fileWatcher.StartWatching();
        SafeInvoke(() =>
        {
            lblWatchStatus.Text = "Auto-watch: Monitoring file changes";
            lblWatchStatus.ForeColor = AppColors.Success;
        });
    }

    private void StopFileWatcher()
    {
        _fileWatcher.StopWatching();
        SafeInvoke(() =>
        {
            lblWatchStatus.Text = "Auto-watch: Disabled";
            lblWatchStatus.ForeColor = AppColors.TextMuted;
        });
    }

    private void ChkAutoWatch_CheckedChanged(object? sender, EventArgs e)
    {
        if (chkAutoWatch.Checked)
            StartFileWatcher();
        else
            StopFileWatcher();
    }

    private void OnWatcherStatus(string status)
    {
        SafeInvoke(() =>
        {
            if (!_searchManager.IsIndexing)
            {
                lblWatchStatus.Text = $"Auto-watch: {status}";
                lblWatchStatus.ForeColor = status.StartsWith("✓") ? AppColors.Success :
                                           status.StartsWith("⚠") ? AppColors.Warning :
                                           AppColors.Success;
            }
            if (status.Contains("Processed"))
                _ = UpdateTotalCountAsync();
        });
    }

    #endregion
}