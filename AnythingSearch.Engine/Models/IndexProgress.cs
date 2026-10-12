namespace AnythingSearch.Models;

public class IndexProgress
{
    public long TotalFiles { get; set; }
    public long TotalFolders { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
    public int PercentComplete { get; set; }
    public double ItemsPerSecond { get; set; }

    /// <summary>Which phase is running: priority files, data drives, or the OS drive.</summary>
    public IndexPhase Phase { get; set; } = IndexPhase.Priority;

    /// <summary>Human-readable name of the scope being indexed, e.g. "Drive D:\".</summary>
    public string PhaseLabel { get; set; } = string.Empty;

    /// <summary>Scopes finished so far, out of <see cref="TotalScopes"/>.</summary>
    public int CompletedScopes { get; set; }

    public int TotalScopes { get; set; }

    /// <summary>
    /// Whether any scope has already been published, i.e. whether the user can search yet.
    /// Search stays locked until this becomes true.
    /// </summary>
    public bool IsSearchable { get; set; }
}
