namespace AnythingSearch.Models;

/// <summary>
/// A single debounced file-system change queued by FileWatcherService.
/// </summary>
public class FileSystemChange
{
    public ChangeType Type { get; set; }
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public DateTime Timestamp { get; set; }
}

public enum ChangeType
{
    Created,
    Deleted,
    Renamed,
    Modified
}
