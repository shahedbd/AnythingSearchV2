using AnythingSearch.Helper;
using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Database-writing consumer side of <see cref="BackgroundIndexingService"/>, plus progress
/// reporting. One consumer per chunk: the shared SQLite connection has a single writer, so extra
/// consumers only contended for the same lock.
/// </summary>
public partial class BackgroundIndexingService
{
    private long _sinceProgressReport;

    private async Task ConsumerAsync()
    {
        var batch = new List<FileEntry>(ConsumerBatchSize);
        var reader = _channel!.Reader;

        try
        {
            // Cancellation is handled by the writer side completing the channel, so this loop
            // drains whatever was already produced instead of discarding it.
            while (await reader.WaitToReadAsync(CancellationToken.None))
            {
                while (batch.Count < ConsumerBatchSize && reader.TryRead(out var entry))
                    batch.Add(entry);

                if (batch.Count == 0) continue;

                await WriteBatchAsync(batch);
                batch.Clear();
            }

            while (reader.TryRead(out var entry))
                batch.Add(entry);

            if (batch.Count > 0)
                await WriteBatchAsync(batch);
        }
        catch (Exception ex)
        {
            Logger.Log($"Indexing writer stopped early - entries may be re-indexed: {ex.Message}");
        }
    }

    private async Task WriteBatchAsync(List<FileEntry> batch)
    {
        foreach (var entry in batch)
            await _database.InsertAsync(entry);

        if (Interlocked.Add(ref _sinceProgressReport, batch.Count) < ProgressReportInterval) return;

        Interlocked.Exchange(ref _sinceProgressReport, 0);
        ReportProgress();
    }

    private void ReportProgress(string? message = null)
    {
        var elapsed = _stopwatch.Elapsed.TotalSeconds;
        var files = Interlocked.Read(ref _totalFiles);
        var folders = Interlocked.Read(ref _totalFolders);
        var speed = elapsed > 0 ? (files + folders) / elapsed : 0;

        _status.UpdateProgress(files, folders, speed, message ?? _currentPath);

        ProgressChanged?.Invoke(new IndexProgress
        {
            TotalFiles = files,
            TotalFolders = folders,
            CurrentPath = message ?? _currentPath,
            PercentComplete = _totalScopes > 0 ? _completedScopes * 100 / _totalScopes : 0,
            ItemsPerSecond = speed,
            Phase = _currentPhase,
            PhaseLabel = _phaseLabel,
            CompletedScopes = _completedScopes,
            TotalScopes = _totalScopes,
            IsSearchable = _hasSearchableData
        });
    }
}
