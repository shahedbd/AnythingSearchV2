namespace AnythingSearch.Models;

public class AppSettings
{
    public List<string> ExcludedFolders { get; set; } = new()
    {
        // Development / Build
        "obj",
        "bin",
        "node_modules",
        ".git",
        ".github",
        ".vs",
        ".idea",
        ".vscode",
        "packages",

        // Package / Dependency Caches
        "AppData\\Local\\NuGet",
        "AppData\\Local\\npm-cache",
        "AppData\\Roaming\\npm-cache",
        "AppData\\Local\\Yarn",
        "AppData\\Local\\pnpm-store",

        // Windows / System
        "$RECYCLE.BIN",
        "System Volume Information",
        "Windows\\WinSxS",

        // Temporary / Cache / Crash Data
        "AppData\\Local\\Temp",
        "AppData\\Local\\CrashDumps",
        "AppData\\Local\\D3DSCache",
        "AppData\\Local\\Microsoft\\Windows\\INetCache",

        // Browser Data
        "AppData\\Local\\Microsoft\\Edge\\User Data",
        "AppData\\Local\\Google\\Chrome\\User Data",
        "AppData\\Local\\Packages"
    };

    public List<string> ExcludedExtensions { get; set; } = new()
    {
        "tmp",
        "temp",
        "cache"
    };

    public bool IndexSystemDrive { get; set; } = true;

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
    /// Entries one indexing scope may add before what it has indexed so far is published as a
    /// sub-phase and becomes searchable. A large data drive (1.4 million files is not unusual)
    /// would otherwise stay invisible for its entire walk, because - unlike the OS drive - there
    /// is no way to know in advance where to divide it. Set to 0 to publish only whole drives.
    /// </summary>
    public int LargeScopeSegmentItems { get; set; } = 500_000;

    /// <summary>
    /// Start the app automatically when Windows starts
    /// </summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// Minimize to system tray instead of closing
    /// </summary>
    public bool MinimizeToTray { get; set; } = true;


    // ── Installation / telemetry ─────────────────────────────────────
    public int AppVersion { get; set; } = 0;
    public int LaunchCount { get; set; } = 0;
    public string LastAppVersion { get; set; } = string.Empty;
    public DateTime? LastUpdateDate { get; set; } = null;
    public DateTime? LastPromotionDate { get; set; } = null;
    public int PromotionCount { get; set; }
    public bool IsNewInstallation { get; set; } = true;
}