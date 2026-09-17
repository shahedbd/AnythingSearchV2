using System.Collections.Concurrent;
using System.Threading.Channels;
using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// The phase/scope/checkpoint loop of <see cref="BackgroundIndexingService"/>. Scopes run
/// strictly one at a time - never two drives at once - and each is processed in small chunks of
/// units. A chunk is committed to the database and recorded in the state file together, so a
/// checkpoint always means "these entries are durably stored": an interruption costs at most the
/// chunk that was in flight.
/// </summary>
public partial class BackgroundIndexingService
{
    /// <summary>
    /// Reconcile the persisted state with the drives currently attached, dropping scopes for
    /// drives that are gone so a removed disk cannot keep the index permanently "incomplete".
    /// </summary>
    private List<IndexScopeDefinition> SyncPlanWithState()
    {
        var scopes = _planner.BuildScopes();

        lock (_stateLock)
        {
            _state.RemoveScopesMissingFrom(scopes.Select(s => s.Key));
            foreach (var scope in scopes)
                _state.GetOrAdd(scope.Key, scope.Phase, scope.Drive);

            _state.StartedAt ??= DateTime.Now;
            _state.Save();
        }

        return scopes;
    }

    private IndexScopeState StateOf(IndexScopeDefinition scope)
    {
        lock (_stateLock)
            return _state.GetOrAdd(scope.Key, scope.Phase, scope.Drive);
    }

    private async Task RunPipelineAsync(bool fullRebuild, CancellationToken token)
    {
        try
        {
            if (fullRebuild)
            {
                _hasSearchableData = false;
                lock (_stateLock) _state.Reset();
                await _database.ClearAsync();
            }

            _status.MarkIndexingStarted();
            await _database.PrepareForBulkIndexingAsync();

            var scopes = SyncPlanWithState();
            _totalScopes = scopes.Count;

            lock (_stateLock)
            {
                _completedScopes = _state.Scopes.Count(s => s.Status == IndexScopeStatus.Completed);
                _totalFiles = _state.TotalFiles;
                _totalFolders = _state.TotalFolders;
            }

            foreach (var scope in scopes)
            {
                if (token.IsCancellationRequested) break;

                var scopeState = StateOf(scope);
                if (scopeState.Status == IndexScopeStatus.Completed) continue;

                await RunScopeAsync(scope, scopeState, token);
            }

            if (token.IsCancellationRequested)
            {
                _isIndexing = false;
                _status.MarkFailed("Indexing was cancelled - progress has been saved");
                ReportProgress("Indexing paused - progress saved, it will resume later");
                return;
            }

            await _database.FinalizeIndexingAsync();

            lock (_stateLock)
            {
                _state.CompletedAt = DateTime.Now;
                _state.Save();
            }

            _stopwatch.Stop();
            _isIndexing = false;
            _hasSearchableData = true;
            _status.MarkCompleted(_totalFiles, _totalFolders);

            var total = _totalFiles + _totalFolders;
            var speed = _stopwatch.Elapsed.TotalSeconds > 0 ? total / _stopwatch.Elapsed.TotalSeconds : 0;
            ReportProgress($"Index complete - {total:N0} items in {_stopwatch.Elapsed:mm\\:ss} ({speed:N0}/sec)");

            IndexingCompleted?.Invoke();
            DatabaseReady?.Invoke();
        }
        catch (OperationCanceledException)
        {
            _isIndexing = false;
            _status.MarkFailed("Indexing was cancelled - progress has been saved");
            ReportProgress("Indexing paused - progress saved, it will resume later");
        }
        catch (Exception ex)
        {
            _isIndexing = false;
            _status.MarkFailed(ex.Message);
            ReportProgress($"Indexing error: {ex.Message}");
            IndexingFailed?.Invoke(ex.Message);
        }
        finally
        {
            _isIndexing = false;
        }
    }

    /// <summary>
    /// Index one scope: phase 1 as a whole, one drive, or one OS-drive sub-phase.
    ///
    /// Normally the scope publishes - becomes searchable - once all of its units are committed.
    /// A scope that keeps growing publishes what it has every
    /// <see cref="AppSettings.LargeScopeSegmentItems"/> entries instead, so a 1.4-million-file
    /// drive does not stay invisible for its whole walk (see
    /// BackgroundIndexingService.Segments.cs).
    /// </summary>
    private async Task RunScopeAsync(
        IndexScopeDefinition scope, IndexScopeState scopeState, CancellationToken token)
    {
        _currentPhase = scope.Phase;
        Interlocked.Exchange(ref _scopeFiles, 0);
        Interlocked.Exchange(ref _scopeFolders, 0);

        long baseFiles;
        long baseFolders;
        long segmentBase;

        lock (_stateLock)
        {
            scopeState.Status = IndexScopeStatus.InProgress;
            scopeState.StartedAt ??= DateTime.Now;
            scopeState.Error = null;
            baseFiles = scopeState.Files;
            baseFolders = scopeState.Folders;

            // A resumed scope continues from what it had already published, so the next sub-phase
            // is another threshold's worth of entries away rather than immediate.
            segmentBase = scopeState.Items;
            _phaseLabel = SegmentLabel(scope, scopeState, final: false);
            _state.Save();
        }

        ReportProgress($"Phase {(int)scope.Phase} - indexing {scope.Label}...");

        var alreadyDone = new HashSet<string>(scopeState.CompletedUnits, StringComparer.OrdinalIgnoreCase);
        var units = _planner.ExpandUnits(scope).Where(u => !alreadyDone.Contains(u.Key)).ToList();
        var skipDirectories = SkipDirectoriesFor(scope);
        var chunkSize = Math.Max(1, _settingsManager.Settings.MaxIndexingThreads);

        var segmentThreshold = SegmentThreshold;

        for (int offset = 0; offset < units.Count; offset += chunkSize)
        {
            if (token.IsCancellationRequested) break;

            var chunk = units.GetRange(offset, Math.Min(chunkSize, units.Count - offset));
            await RunChunkAsync(chunk, scopeState, skipDirectories, baseFiles, baseFolders, token);

            if (token.IsCancellationRequested) break;
            if (segmentThreshold == 0) continue;

            // Nothing to gain from a sub-phase on the last chunk - the scope itself is about to
            // publish - so this only fires while there is still work left in the scope.
            if (offset + chunkSize >= units.Count) continue;

            long indexed;
            lock (_stateLock) indexed = scopeState.Items;

            if (indexed - segmentBase < segmentThreshold) continue;

            segmentBase = indexed;
            await PublishSegmentAsync(scope, scopeState);
        }

        if (token.IsCancellationRequested) return;

        // Light per-scope wrap-up: fold the write-ahead log back into the database file so the
        // next snapshot build reads one file. The expensive ANALYZE/VACUUM waits for the end.
        await _database.FinalizeScopeAsync();

        lock (_stateLock)
        {
            scopeState.Status = IndexScopeStatus.Completed;
            scopeState.CompletedAt = DateTime.Now;
            scopeState.Error = null;

            // The checkpoint list only exists to let an interrupted scope resume; a completed
            // scope is skipped wholesale, so the list is dead weight in the state file.
            scopeState.CompletedUnits.Clear();
            _state.Save();
        }

        _completedScopes++;

        // A scope that found nothing (no Downloads folder, no recent files) must not unlock
        // search - there would be nothing to find, and the status would be misleading.
        if (scopeState.Items == 0 && !_hasSearchableData)
        {
            ReportProgress($"{scope.Label}: nothing to index");
            return;
        }

        _hasSearchableData = true;

        var label = SegmentLabel(scope, scopeState, final: true);
        ReportProgress($"{label} indexed - {scopeState.Items:N0} items, now searchable");
        ScopePublished?.Invoke(label);
    }

    /// <summary>
    /// Walk a handful of units in parallel, then commit them and record the checkpoint. Commit
    /// and checkpoint happen together so the state file never claims progress that is not stored.
    /// </summary>
    private async Task RunChunkAsync(
        List<ScanRoot> chunk,
        IndexScopeState scopeState,
        HashSet<string> skipDirectories,
        long baseFiles,
        long baseFolders,
        CancellationToken token)
    {
        _channel = Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var failures = new ConcurrentBag<string>();

        await _database.BeginBatchAsync();
        var consumer = Task.Run(() => ConsumerAsync(), CancellationToken.None);

        try
        {
            // The token is checked in the body, not handed to ParallelOptions: that overload
            // throws, and a cancelled run here is an orderly stop rather than an error.
            await Task.Run(() => Parallel.ForEach(
                chunk,
                new ParallelOptions { MaxDegreeOfParallelism = chunk.Count },
                unit =>
                {
                    if (token.IsCancellationRequested) return;

                    try { ScanUnit(unit, skipDirectories, token); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { failures.Add($"{unit.Directory.FullName}: {ex.Message}"); }
                }), CancellationToken.None);
        }
        finally
        {
            _channel.Writer.TryComplete();
            await consumer;

            // Commit even when cancelled: the entries already handed to the writer belong in the
            // database, and leaving an open transaction behind would block every later write.
            await _database.CommitBatchAsync();
        }

        lock (_stateLock)
        {
            scopeState.Files = baseFiles + Interlocked.Read(ref _scopeFiles);
            scopeState.Folders = baseFolders + Interlocked.Read(ref _scopeFolders);

            foreach (var failure in failures)
            {
                if (!scopeState.FailedUnits.Contains(failure))
                    scopeState.FailedUnits.Add(failure);
            }

            if (!failures.IsEmpty)
                scopeState.Error = $"{scopeState.FailedUnits.Count} location(s) could not be read";

            // Only a chunk that ran to completion may be checkpointed - a cancelled one is
            // re-walked next time, and the unique index absorbs anything written twice.
            if (!token.IsCancellationRequested)
            {
                foreach (var unit in chunk)
                    scopeState.CompletedUnits.Add(unit.Key);

                scopeState.LastCheckpoint = chunk[chunk.Count - 1].Directory.FullName;
            }

            _state.Save();
        }

        ReportProgress();
    }

    /// <summary>
    /// Directories a scope must not walk because an earlier phase already covered them in full.
    /// Without this the OS-drive phase would walk Downloads a second time.
    /// </summary>
    private HashSet<string> SkipDirectoriesFor(IndexScopeDefinition scope)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scope.Phase != IndexPhase.SystemDrive) return skip;

        IndexScopeState? priority;
        lock (_stateLock)
            priority = _state.Scopes.FirstOrDefault(s => s.Phase == IndexPhase.Priority);

        // Only skip when phase 1 actually finished - an interrupted phase 1 leaves Downloads
        // partly indexed, and the OS drive pass is then the only thing that will complete it.
        if (priority?.Status != IndexScopeStatus.Completed) return skip;

        var downloads = IndexPlanner.DownloadsPath.TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.Exists(downloads))
            skip.Add(downloads);

        return skip;
    }
}
