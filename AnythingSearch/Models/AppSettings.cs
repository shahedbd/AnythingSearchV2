namespace AnythingSearch.Models;

public class AppSettings
{
    public List<string> ExcludedFolders { get; set; } = new()
    {
        "obj",
        "bin",
        "node_modules",
        ".git",
        ".vs",
        "packages",
        "$RECYCLE.BIN",
        "System Volume Information",
        "Windows\\WinSxS",
        "AppData\\Local\\Temp",

        //dev test folders
        //"C:\\Windows",
        //"E:\\Personal Galary",
        //"G:\\Personal Gallery-2",
        //"C:\\Users\\Public\\src",
        //"G:\\TheGitCloning"

    };

    public List<string> ExcludedExtensions { get; set; } = new()
    {
        "tmp",
        "temp",
        "cache"
    };

    public bool IndexSystemDrive { get; set; } = true;
    public int BatchSize { get; set; } = 5000;

    /// <summary>
    /// How many scan units are walked at the same time within one drive. Deliberately low:
    /// the old indexer ran one walker per CPU core over hundreds of directories, which turns a
    /// mechanical disk into a seek storm and pins it at 100% for the whole run. Two concurrent
    /// walkers keep an SSD busy without thrashing an HDD.
    /// </summary>
    public int MaxIndexingThreads { get; set; } = 2;

    /// <summary>
    /// Entries a walker emits between throttle pauses. Together with
    /// <see cref="IndexThrottleDelayMs"/> this caps sustained disk pressure so the machine stays
    /// usable while indexing. Set the delay to 0 to index as fast as the disk allows.
    /// </summary>
    public int IndexThrottleBatchSize { get; set; } = 2000;

    /// <summary>Pause in milliseconds after each throttle batch. 0 disables throttling.</summary>
    public int IndexThrottleDelayMs { get; set; } = 4;

    /// <summary>
    /// Flag to track if this is the first time the app is running.
    /// When true, the app will automatically start indexing.
    /// </summary>
    public bool IsFirstRun { get; set; } = true;

    /// <summary>
    /// Start the app automatically when Windows starts
    /// </summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// Minimize to system tray instead of closing
    /// </summary>
    public bool MinimizeToTray { get; set; } = true;
}