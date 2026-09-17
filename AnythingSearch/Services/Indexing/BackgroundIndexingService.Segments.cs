using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Splits an oversized scope into sub-phases while it runs.
///
/// The OS drive is divided up in advance, because Windows tells us where its big directories are
/// (see IndexPlanner.SystemDrive.cs). A data drive gives us nothing to go on: a 1.4-million-file
/// drive can be one folder or a thousand, and the only way to know how much is in a directory is
/// to walk it - which is the work we are trying to divide. Counting first would mean reading every
/// drive twice.
///
/// So a data drive is segmented by what has actually been indexed. The scope's units are already
/// processed in committed chunks; once a scope has added
/// <see cref="AppSettings.LargeScopeSegmentItems"/> entries since its last publish, and there are
/// still chunks to go, the entries so far are published as a sub-phase and become searchable.
/// A 1.6-million-entry drive therefore publishes three times instead of going dark for its whole
/// walk, and a 200,000-entry drive publishes once, with no pre-pass and no assumption about how
/// the user organises their disks.
///
/// Segments are publish points, not checkpoints: resume is still driven by
/// <see cref="IndexScopeState.CompletedUnits"/>, so a segment boundary costs nothing if the app
/// is closed mid-scope.
/// </summary>
public partial class BackgroundIndexingService
{
    /// <summary>
    /// Publish everything committed in this scope so far as a sub-phase. Called between chunks,
    /// so the data is already in the database - this only checkpoints the WAL, records the
    /// segment, and tells the search layer to pick it up.
    /// </summary>
    private async Task PublishSegmentAsync(IndexScopeDefinition scope, IndexScopeState scopeState)
    {
        // Fold the write-ahead log back in first: the snapshot rebuild that follows reads the
        // database file, and a large WAL would make it read two.
        await _database.FinalizeScopeAsync();

        int index;
        long items;

        lock (_stateLock)
        {
            index = scopeState.Segments.Count + 1;
            items = scopeState.Items;

            scopeState.Segments.Add(new IndexSegmentState
            {
                Index = index,
                Items = items,
                LastUnit = scopeState.LastCheckpoint,
                CompletedAt = DateTime.Now
            });

            _state.Save();
        }

        _hasSearchableData = true;

        var label = $"{scope.Label} (part {index})";
        _phaseLabel = $"{scope.Label} (part {index + 1})";

        ReportProgress($"{label} indexed - {items:N0} items so far, now searchable");
        ScopePublished?.Invoke(label);
    }

    /// <summary>
    /// The name to show for a scope, which includes the sub-phase number once a scope has been
    /// segmented. A scope small enough never to be split keeps its plain label.
    /// </summary>
    private static string SegmentLabel(IndexScopeDefinition scope, IndexScopeState scopeState, bool final)
    {
        var parts = scopeState.Segments.Count;
        if (parts == 0) return scope.Label;

        return final
            ? $"{scope.Label} (part {parts + 1}, final)"
            : $"{scope.Label} (part {parts + 1})";
    }

    /// <summary>
    /// Entries a scope may add before its next sub-phase is published, or 0 when segmentation is
    /// switched off in settings.
    /// </summary>
    private long SegmentThreshold => Math.Max(0, _settingsManager.Settings.LargeScopeSegmentItems);
}
