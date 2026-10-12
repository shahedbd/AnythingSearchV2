namespace AnythingSearch.Models;

/// <summary>
/// Anything Search settings. The index settings (exclusions, system drive, indexing pace) live in
/// the engine's <see cref="IndexSettings"/>, which the engine reads through EngineOptions.Settings.
/// </summary>
public class AppSettings : IndexSettings
{
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