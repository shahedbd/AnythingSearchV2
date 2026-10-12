namespace AnythingSearch.Models;

/// <summary>
/// A single debounced file-system change queued by FileWatcherService.
/// </summary>
public class FileSystemChange
{
    public ChangeType Type { get; set; }
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    /// <summary>Time of the most recent OS event for this path (used for debouncing).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Time the path first entered the queue. A path that keeps receiving events (a log file,
    /// a download in progress) would otherwise refresh <see cref="Timestamp"/> forever and never
    /// be flushed, eventually filling the queue and blocking every other change.
    /// </summary>
    public DateTime FirstSeen { get; set; }
}

public enum ChangeType
{
    Created,
    Deleted,
    Renamed,
    Modified
}
