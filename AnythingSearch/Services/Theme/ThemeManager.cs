namespace AnythingSearch.Services;

/// <summary>
/// Manages application-wide theme settings using the Singleton pattern.
/// Provides dark and light theme configurations with event-driven theme changes.
/// Ported from NetSpeedMeterPlus' ThemeManager; the palette roles here are the ones
/// MainForm.AppColors exposes, and the light theme reproduces the original palette exactly.
/// </summary>
public sealed class ThemeManager
{
    #region Singleton Implementation

    private static readonly Lazy<ThemeManager> _instance = new(() => new ThemeManager());

    /// <summary>Gets the singleton instance of ThemeManager.</summary>
    public static ThemeManager Instance => _instance.Value;

    #endregion

    #region Properties

    /// <summary>Gets the currently active theme configuration.</summary>
    public AppTheme CurrentTheme { get; private set; }

    /// <summary>Gets a value indicating whether the dark theme is currently active.</summary>
    public bool IsDarkTheme { get; private set; }

    #endregion

    #region Events

    /// <summary>Occurs when the application theme is changed.</summary>
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    #endregion

    #region Constructor

    private ThemeManager()
    {
        IsDarkTheme = SettingsService.Current.IsDarkMode;
        CurrentTheme = IsDarkTheme ? CreateDarkTheme() : CreateLightTheme();
    }

    #endregion

    #region Public Methods

    /// <summary>Toggles between dark and light themes.</summary>
    public void ToggleTheme() => SetTheme(!IsDarkTheme);

    /// <summary>Sets the application theme to dark or light mode and persists the choice.</summary>
    public void SetTheme(bool isDark)
    {
        if (IsDarkTheme == isDark)
            return; // No change needed

        var previousTheme = IsDarkTheme;
        IsDarkTheme = isDark;
        CurrentTheme = isDark ? CreateDarkTheme() : CreateLightTheme();

        SettingsService.Current.IsDarkMode = isDark;
        try { SettingsService.Save(); }
        catch { /* already logged by SettingsService; the theme still applies this session */ }

        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(previousTheme, IsDarkTheme));
    }

    /// <summary>Gets a theme configuration by type.</summary>
    public AppTheme GetTheme(bool isDark) => isDark ? CreateDarkTheme() : CreateLightTheme();

    #endregion

    #region Private Methods - Theme Creation

    private static AppTheme CreateDarkTheme() => new()
    {
        // Accent - lighter than the light theme's so it holds contrast on dark surfaces
        Primary = Rgb(96, 205, 255),
        PrimaryDark = Rgb(0, 120, 212),
        PrimaryLight = Rgb(153, 235, 255),
        Link = Rgb(96, 205, 255),
        SelectionBack = Rgb(0, 120, 215),

        // Backgrounds
        Background = Rgb(32, 32, 32),
        Surface = Rgb(43, 43, 43),
        SurfaceAlt = Rgb(38, 38, 38),
        StatusBar = Rgb(28, 28, 28),
        GridHeader = Rgb(50, 50, 50),
        Hover = Rgb(55, 55, 55),
        Selected = Rgb(45, 62, 85),
        DangerHover = Rgb(80, 40, 40),

        // Borders
        Border = Rgb(64, 64, 64),
        BorderFocus = Rgb(96, 205, 255),

        // Text
        TextPrimary = Rgb(240, 240, 240),
        TextSecondary = Rgb(200, 200, 200),
        TextMuted = Rgb(150, 150, 150),
        DangerText = Rgb(255, 120, 120),

        // Status
        Success = Rgb(108, 203, 95),
        Warning = Rgb(252, 185, 0),
        Error = Rgb(255, 153, 164),

        Name = "Dark",
        IsDark = true
    };

    private static AppTheme CreateLightTheme() => new()
    {
        Primary = Rgb(0, 120, 212),
        PrimaryDark = Rgb(0, 99, 177),
        PrimaryLight = Rgb(0, 140, 240),
        Link = Rgb(0, 102, 204),
        SelectionBack = Rgb(0, 120, 215),

        Background = Rgb(249, 249, 249),
        Surface = Color.White,
        SurfaceAlt = Rgb(252, 252, 252),
        StatusBar = Rgb(243, 243, 243),
        GridHeader = Rgb(240, 240, 240),
        Hover = Rgb(243, 243, 243),
        Selected = Rgb(232, 240, 254),
        DangerHover = Rgb(255, 235, 235),

        Border = Rgb(229, 229, 229),
        BorderFocus = Rgb(0, 120, 212),

        TextPrimary = Rgb(32, 32, 32),
        TextSecondary = Rgb(96, 96, 96),
        TextMuted = Rgb(136, 136, 136),
        DangerText = Rgb(220, 80, 80),

        Success = Rgb(16, 124, 16),
        Warning = Rgb(255, 140, 0),
        Error = Rgb(196, 43, 28),

        Name = "Light",
        IsDark = false
    };

    private static Color Rgb(int r, int g, int b) => Color.FromArgb(255, r, g, b);

    #endregion
}
