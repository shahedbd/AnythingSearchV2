using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnythingSearch.Models;

/// <summary>
/// The indexing phases, run strictly in order. Each phase is made of one or more scopes, and a
/// scope's data becomes searchable as soon as that scope finishes - so the app is usable long
/// before the whole disk has been read.
/// </summary>
public enum IndexPhase
{
    /// <summary>Phase 1: Downloads plus the directories holding recently used files.</summary>
    Priority = 1,

    /// <summary>Phase 2: every non-OS fixed drive, one drive at a time.</summary>
    DataDrives = 2,

    /// <summary>Phase 3: the OS drive, published only once it has finished.</summary>
    SystemDrive = 3
}

public enum IndexScopeStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}

/// <summary>
/// One published sub-phase of a scope that grew past
/// <see cref="AppSettings.LargeScopeSegmentItems"/> entries. Recorded for display and
/// diagnostics: resume is driven by <see cref="IndexScopeState.CompletedUnits"/>, not by these.
/// </summary>
public class IndexSegmentState
{
    public int Index { get; set; }

    /// <summary>Cumulative items indexed in the scope when this sub-phase was published.</summary>
    public long Items { get; set; }

    /// <summary>Last unit committed before this sub-phase was published.</summary>
    public string LastUnit { get; set; } = "";

    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Persisted progress for one scope (phase 1 as a whole, or a single drive).
/// <see cref="CompletedUnits"/> is the checkpoint: the units already committed to the database,
/// which a resumed run skips instead of walking again.
/// </summary>
public class IndexScopeState
{
    public string Key { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IndexPhase Phase { get; set; }

    public string Drive { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IndexScopeStatus Status { get; set; } = IndexScopeStatus.Pending;

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long Files { get; set; }
    public long Folders { get; set; }

    /// <summary>Last unit committed, kept for display and diagnostics.</summary>
    public string LastCheckpoint { get; set; } = "";

    /// <summary>Units whose entries are committed to the database. Resume skips these.</summary>
    public List<string> CompletedUnits { get; set; } = new();

    /// <summary>Last failure seen for this scope, cleared when it eventually completes.</summary>
    public string? Error { get; set; }

    /// <summary>Units that could not be read, so the user can see what was skipped.</summary>
    public List<string> FailedUnits { get; set; } = new();

    /// <summary>
    /// Sub-phases already published for this scope. Empty for a scope small enough to publish in
    /// one go, which is the common case.
    /// </summary>
    public List<IndexSegmentState> Segments { get; set; } = new();

    [JsonIgnore]
    public long Items => Files + Folders;
}

/// <summary>
/// The persistent indexing state file (indexing_state.json), written next to the database.
///
/// This is what makes indexing resumable: the previous run's phases, per-drive status, committed
/// checkpoints, completion flags and failures all survive a restart, so the next startup only
/// indexes what is still missing instead of rebuilding 3+ million entries from scratch.
/// </summary>
public class IndexingState
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AnythingSearch",
        "indexing_state.json");

    /// <summary>Where this instance persists to. Private, so it is never serialized.</summary>
    private string _filePath = DefaultPath;

    public int SchemaVersion { get; set; } = 1;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastUpdatedAt { get; set; }

    /// <summary>Per-scope progress, in the order the scopes are run.</summary>
    public List<IndexScopeState> Scopes { get; set; } = new();

    /// <summary>True once every known scope has completed at least once.</summary>
    [JsonIgnore]
    public bool IsComplete => Scopes.Count > 0 && Scopes.All(s => s.Status == IndexScopeStatus.Completed);

    /// <summary>True when some scope has completed, so part of the index is already searchable.</summary>
    [JsonIgnore]
    public bool HasSearchableData => Scopes.Any(s => s.Status == IndexScopeStatus.Completed);

    [JsonIgnore]
    public long TotalFiles => Scopes.Sum(s => s.Files);

    [JsonIgnore]
    public long TotalFolders => Scopes.Sum(s => s.Folders);

    public static IndexingState Load(string? filePathOverride = null)
    {
        var path = filePathOverride ?? DefaultPath;

        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<IndexingState>(File.ReadAllText(path));
                if (state != null && state.SchemaVersion == 1)
                {
                    state._filePath = path;

                    // A scope left InProgress means the app stopped mid-scope. Its committed
                    // units are still valid, so it is put back to Pending and resumes from them.
                    foreach (var scope in state.Scopes)
                    {
                        if (scope.Status == IndexScopeStatus.InProgress)
                            scope.Status = IndexScopeStatus.Pending;
                    }

                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IndexingState] Load failed: {ex.Message}");
        }

        return new IndexingState { _filePath = path };
    }

    public void Save()
    {
        try
        {
            LastUpdatedAt = DateTime.Now;

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_filePath, JsonSerializer.Serialize(
                this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IndexingState] Save failed: {ex.Message}");
        }
    }

    /// <summary>Forget all progress, so the next run indexes everything again.</summary>
    public void Reset()
    {
        Scopes.Clear();
        StartedAt = DateTime.Now;
        CompletedAt = null;
        Save();
    }

    /// <summary>
    /// The stored state for a scope, created on first sight. Scope keys are stable across runs
    /// (they are derived from the phase and the drive), which is what lets a run resume.
    /// </summary>
    public IndexScopeState GetOrAdd(string key, IndexPhase phase, string drive)
    {
        var existing = Scopes.FirstOrDefault(s => s.Key == key);
        if (existing != null) return existing;

        var created = new IndexScopeState { Key = key, Phase = phase, Drive = drive };
        Scopes.Add(created);
        return created;
    }

    /// <summary>Drop stored scopes that no longer exist, e.g. a drive that was removed.</summary>
    public void RemoveScopesMissingFrom(IEnumerable<string> liveKeys)
    {
        var live = new HashSet<string>(liveKeys, StringComparer.OrdinalIgnoreCase);
        Scopes.RemoveAll(s => !live.Contains(s.Key));
    }
}
