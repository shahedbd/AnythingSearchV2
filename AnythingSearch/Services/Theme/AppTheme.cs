namespace AnythingSearch.Services;

/// <summary>
/// Represents a complete theme configuration for the application.
/// Read through MainForm.AppColors, which always resolves against ThemeManager.CurrentTheme.
/// </summary>
public sealed class AppTheme
{
    #region Accent Colors

    public Color Primary { get; init; }
    public Color PrimaryDark { get; init; }
    public Color PrimaryLight { get; init; }
    public Color Link { get; init; }

    /// <summary>Results-grid row selection background.</summary>
    public Color SelectionBack { get; init; }

    #endregion

    #region Background Colors

    public Color Background { get; init; }
    public Color Surface { get; init; }

    /// <summary>Alternating results-grid row.</summary>
    public Color SurfaceAlt { get; init; }
    public Color StatusBar { get; init; }
    public Color GridHeader { get; init; }
    public Color Hover { get; init; }
    public Color Selected { get; init; }

    /// <summary>Hover background of destructive buttons (e.g. remove recent search).</summary>
    public Color DangerHover { get; init; }

    #endregion

    #region Border Colors

    public Color Border { get; init; }
    public Color BorderFocus { get; init; }

    #endregion

    #region Text Colors

    public Color TextPrimary { get; init; }
    public Color TextSecondary { get; init; }
    public Color TextMuted { get; init; }
    public Color DangerText { get; init; }

    #endregion

    #region Status Colors

    public Color Success { get; init; }
    public Color Warning { get; init; }
    public Color Error { get; init; }

    #endregion

    #region Metadata

    public string Name { get; init; } = string.Empty;
    public bool IsDark { get; init; }

    #endregion
}

/// <summary>Provides data for the ThemeChanged event.</summary>
public class ThemeChangedEventArgs : EventArgs
{
    /// <summary>Gets whether the previous theme was dark.</summary>
    public bool WasDarkTheme { get; }

    /// <summary>Gets whether the new theme is dark.</summary>
    public bool IsDarkTheme { get; }

    public ThemeChangedEventArgs(bool wasDark, bool isDark)
    {
        WasDarkTheme = wasDark;
        IsDarkTheme = isDark;
    }
}
