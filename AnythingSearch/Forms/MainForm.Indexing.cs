using AnythingSearch.Models;
using AnythingSearch.Services;

namespace AnythingSearch.Forms;

/// <summary>
/// Indexing functionality for MainForm.
///
/// The Index button drives the exact same phased pipeline as startup - it either resumes an
/// interrupted index or rebuilds from scratch, and both run in the background without blocking
/// the UI thread. There is no separate "manual indexing" code path any more: the old button
/// handler kicked off its own full-drive scan, which is what froze the window and pinned the disk.
/// </summary>
public partial class MainForm
{
    #region Index Button Handler

    private async void BtnIndex_Click(object? sender, EventArgs e)
    {
        if (_searchManager.IsIndexing)
        {
            OfferToPauseIndexing();
            return;
        }

        // An index that stopped part way through: resuming is far cheaper than starting over,
        // so that is offered first.
        var state = _searchManager.IndexingState;
        if (!state.IsComplete && state.HasSearchableData)
        {
            var pending = state.Scopes.Count(s => s.Status != IndexScopeStatus.Completed);
            var resume = MessageBox.Show(
                "Resume Indexing?\n\n" +
                $"Indexed so far: {state.TotalFiles + state.TotalFolders:N0} items\n" +
                $"Still to index: {pending} location(s)\n\n" +
                "Resuming continues from the last checkpoint instead of scanning everything again.\n" +
                "Choose No to rebuild the whole index from scratch.",
                "Resume Indexing",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (resume == DialogResult.Cancel) return;

            if (resume == DialogResult.Yes)
            {
                StopFileWatcher();
                SetIndexingUIState(true);
                await _searchManager.ResumeIndexingAsync();
                return;
            }
        }
        else if (_searchManager.IsDatabaseReady)
        {
            var rebuild = MessageBox.Show(
                "Rebuild Search Index?\n\n" +
                $"Current index: {_searchManager.IndexingStatus.TotalItems:N0} items\n" +
                $"Last updated: {_searchManager.IndexingStatus.IndexingCompletedAt:g}\n\n" +
                "The index is rebuilt in the background, in phases:\n" +
                "• Downloads and recent files first, so search returns quickly\n" +
                "• Then each other drive, one at a time\n" +
                "• The system drive last\n\n" +
                "Search is unavailable until the first phase finishes.",
                "Rebuild Index",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (rebuild != DialogResult.Yes) return;
        }
        else
        {
            var build = MessageBox.Show(
                "Build Search Index?\n\n" +
                "Your drives are scanned in the background, in phases:\n" +
                "• Downloads and recent files first, so search returns within seconds\n" +
                "• Then each other drive, one at a time - searchable as each one finishes\n" +
                "• The system drive last\n\n" +
                "Progress is saved as it goes, so closing the app does not lose the work.",
                "Build Index",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (build != DialogResult.Yes) return;
        }

        // Stop the watcher: it writes to the same database the build is writing to.
        StopFileWatcher();
        SetIndexingUIState(true);
        await _searchManager.RebuildIndexAsync();
    }

    private void OfferToPauseIndexing()
    {
        var cancelResult = MessageBox.Show(
            "Indexing is in progress.\n\n" +
            "Would you like to pause it? Everything indexed so far is kept, and it resumes " +
            "from the last checkpoint next time.",
            "Indexing In Progress",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (cancelResult != DialogResult.Yes) return;

        _searchManager.CancelIndexing();
        lblSearchInfo.Text = "Indexing paused - progress saved";
        UpdateSearchSourceUI();
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

            if (!isIndexing)
            {
                UpdateSearchSourceUI();
                return;
            }

            btnIndex.Text = "⏳ Indexing...";
            ApplySearchLock();
        });
    }

    /// <summary>
    /// Disable the search box while there is nothing to search, and re-enable it the moment the
    /// first phase publishes - which is the whole point of indexing Downloads and recent files
    /// first. The lock only covers the window where a search could return nothing at all.
    /// </summary>
    private void ApplySearchLock()
    {
        var locked = _searchManager.IsSearchLocked;

        txtSearch.Enabled = !locked;

        if (locked)
        {
            dgvResults.Visible = false;
            pnlRecentSearches.Visible = false;
            lblSearchInfo.Text = "App is indexing... search will be available shortly";
            lblWatchStatus.Text = "App is indexing...";
            lblWatchStatus.ForeColor = AppColors.Warning;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(txtSearch.Text) || txtSearch.Text == SearchPlaceholder)
            {
                pnlRecentSearches.Visible = true;
                LoadRecentSearches();
            }

            if (_searchManager.IsIndexing)
            {
                lblWatchStatus.Text = "Indexing continues in the background - search is available";
                lblWatchStatus.ForeColor = AppColors.Warning;
            }
        }
    }

    #endregion

    #region Indexing Events

    private void OnIndexingProgress(IndexProgress progress)
    {
        SafeInvoke(() =>
        {
            if (_searchManager.IsSearchLocked)
            {
                lblSearchInfo.Text = $"App is indexing... {progress.PhaseLabel} " +
                                     $"({progress.TotalFiles + progress.TotalFolders:N0} items)";
            }
            else
            {
                lblSearchInfo.Text =
                    $"📂 {progress.TotalFolders:N0} folders  •  📄 {progress.TotalFiles:N0} files  •  " +
                    $"⚡ {progress.ItemsPerSecond:N0}/sec";
            }

            lblWatchStatus.Text = $"Phase {(int)progress.Phase} of 3 · {progress.PhaseLabel} " +
                                  $"({progress.CompletedScopes}/{progress.TotalScopes} done): " +
                                  TruncatePath(progress.CurrentPath, 60);
            lblWatchStatus.ForeColor = AppColors.Warning;

            UpdateTrayStatus($"Indexing: {progress.TotalFiles + progress.TotalFolders:N0} items...");
        });
    }

    /// <summary>
    /// One phase or drive has finished and its data is searchable. Unlocks the search box on the
    /// first one, and refreshes the counts on every one.
    /// </summary>
    private void OnScopePublished(string scopeLabel)
    {
        SafeInvoke(async () =>
        {
            ApplySearchLock();
            lblWatchStatus.Text = $"✓ {scopeLabel} indexed - now searchable";
            lblWatchStatus.ForeColor = AppColors.Success;
            await UpdateTotalCountAsync();
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
