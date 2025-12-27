namespace AnythingSearch.Forms;

internal static class Program
{
    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // Enable high DPI support for Windows 10/11
        // This is CRITICAL for Microsoft Store approval at 150% scaling
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Enable visual styles for modern appearance
        Application.EnableVisualStyles();

        // Use compatible text rendering for better font scaling
        Application.SetCompatibleTextRenderingDefault(false);

        // Set default font for the entire application (DPI-aware)
        Application.SetDefaultFont(new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point));

        // Run the main form
        Application.Run(new MainForm());
    }
}