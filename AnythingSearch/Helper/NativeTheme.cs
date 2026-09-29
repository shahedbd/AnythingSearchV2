using System.Runtime.InteropServices;

namespace AnythingSearch.Helper;

/// <summary>
/// Dark/light theming for the parts of a window WinForms does not draw itself: the title bar
/// (DWM) and native scrollbars (uxtheme). Cosmetic only - on Windows builds without these APIs
/// the calls fail quietly and the chrome stays light.
/// </summary>
public static class NativeTheme
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    /// <summary>Switches a top-level window's title bar between dark and light.</summary>
    public static void ApplyTitleBar(Form form, bool isDark)
    {
        if (!form.IsHandleCreated) return;
        try
        {
            int value = isDark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
        }
        catch { /* older Windows - keep the light title bar */ }
    }

    /// <summary>Switches the native scrollbars of the given controls between dark and light.</summary>
    public static void ApplyScrollBars(IEnumerable<Control> controls, bool isDark)
    {
        string themeName = isDark ? "DarkMode_Explorer" : "Explorer";
        try
        {
            foreach (var ctl in controls.Where(c => c.IsHandleCreated))
                SetWindowTheme(ctl.Handle, themeName, null);
        }
        catch { /* older Windows - keep light scrollbars */ }
    }
}
