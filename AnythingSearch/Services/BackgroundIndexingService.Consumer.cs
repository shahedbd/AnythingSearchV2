using System.Threading.Channels;
using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Database-writing consumer side of BackgroundIndexingService, plus progress reporting.
/// </summary>
public partial class BackgroundIndexingService
{
    /// <summary>
    /// Consumer task - parallel database writer
    /// </summary>
    private async Task ConsumerAsync(int consumerId, CancellationToken cancellationToken)
    {
        var batch = new List<FileEntry>(ConsumerBatchSize);
        var reader = _channel!.Reader;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (batch.Count < ConsumerBatchSize && reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count > 0)
                {
                    foreach (var entry in batch)
                    {
                        await _database.InsertAsync(entry);
                    }
                    Interlocked.Add(ref _processedItems, batch.Count);
                    batch.Clear();
                }
            }

            while (reader.TryRead(out var entry))
            {
                await _database.InsertAsync(entry);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException)
        {
            while (reader.TryRead(out var entry))
            {
                await _database.InsertAsync(entry);
            }
        }
        catch { }
    }

    private void ReportProgress(string? message = null)
    {
        var elapsed = _stopwatch.Elapsed.TotalSeconds;
        var total = _totalFiles + _totalFolders;
        var speed = elapsed > 0 ? total / elapsed : 0;

        _status.UpdateProgress(_totalFiles, _totalFolders, speed, message ?? _currentPath);

        ProgressChanged?.Invoke(new IndexProgress
        {
            TotalFiles = _totalFiles,
            TotalFolders = _totalFolders,
            CurrentPath = message ?? _currentPath,
            PercentComplete = 0,
            ItemsPerSecond = speed
        });
    }
}
