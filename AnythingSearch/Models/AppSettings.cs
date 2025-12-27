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