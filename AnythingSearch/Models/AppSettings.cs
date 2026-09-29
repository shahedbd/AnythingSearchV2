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
    /// Pace indexing from the hardware each drive actually sits on, rather than from the fixed
    /// numbers below - see <see cref="Services.IndexingCapacity"/> for what is measured and how
    /// it is used. On when nothing says otherwise, including for settings files written before
    /// this existed, because the fixed numbers had to suit the slowest machine the app ships to
    /// and were therefore wrong for most of them.
    ///
    /// Turn it off to pin <see cref="MaxIndexingThreads"/>, <see cref="IndexThrottleBatchSize"/>
    /// and <see cref="IndexThrottleDelayMs"/> to exactly the values set here.
    /// </summary>
    public bool AutoTuneIndexing { get; set; } = true;

    /// <summary>
    /// How many scan units are walked at the same time within one drive, when
    /// <see cref="AutoTuneIndexing"/> is off. Deliberately low: the old indexer ran one walker
    /// per CPU core over hundreds of directories, which turns a mechanical disk into a seek storm
    /// and pins it at 100% for the whole run. Two concurrent walkers keep an SSD busy without
    /// thrashing an HDD - which is the right compromise only when the disk is unknown, and is
    /// why the automatic path exists.
    /// </summary>
    public int MaxIndexingThreads { get; set; } = 2;

    /// <summary>
    /// Entries a walker emits between throttle pauses, when <see cref="AutoTuneIndexing"/> is
    /// off. Together with <see cref="IndexThrottleDelayMs"/> this caps sustained disk pressure so
    /// the machine stays usable while indexing. Set the delay to 0 to index as fast as the disk
    /// allows.
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
    /// Minimize to system tray instead of closing
    /// </summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// Keep the index in sync with the disk: live file-system watching plus the startup
    /// catch-up scan for changes made while the app was closed. Mirrors the main window's
    /// Auto-Watch checkbox.
    /// </summary>
    public bool AutoWatch { get; set; } = true;

    /// <summary>
    /// Dark theme on/off. Toggled by the header's theme button via ThemeManager.
    /// </summary>
    public bool IsDarkMode { get; set; } = false;

    // ── Pro licence cache ────────────────────────────────────────────
    /// <summary>
    /// Last answer the Microsoft Store gave for the Pro add-on. Only an explicit Store reply
    /// changes it, so a failed or offline check never downgrades a paying user.
    /// </summary>
    public bool IsProCached { get; set; } = false;
    public DateTime? LastLicenseCheckUtc { get; set; } = null;

    // ── Installation / telemetry ─────────────────────────────────────
    public int AppVersion { get; set; } = 0;
    public int LaunchCount { get; set; } = 0;
    public string LastAppVersion { get; set; } = string.Empty;
    public DateTime? LastUpdateDate { get; set; } = null;
    public DateTime? LastPromotionDate { get; set; } = null;
    public int PromotionCount { get; set; }
    public bool IsNewInstallation { get; set; } = true;
}