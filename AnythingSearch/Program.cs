using AnythingSearch.Helper;

namespace AnythingSearch.Forms;

internal static class Program
{
    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // First statement in the process, deliberately. Everything below can throw - the
        // single-instance check reads other processes' modules, and MainForm's constructor opens
        // the database and starts background work - and until this runs, a throw anywhere means
        // the raw .NET crash dialog and an empty log.
        CrashHandler.Install();

        if (CommonHelper.PriorProcess() != null)
        {
            MessageBox.Show("Another instance of the app is already running.");
            return;
        }
        // Enable high DPI support for Windows 10/11
        // This is CRITICAL for Microsoft Store approval at 150% scaling
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Enable visual styles for modern appearance
        Application.EnableVisualStyles();

        // Use compatible text rendering for better font scaling
        Application.SetCompatibleTextRenderingDefault(false);

        // Set default font for the entire application (DPI-aware)
        Application.SetDefaultFont(new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point));

        // Run the main form. The guard covers the two windows Application.ThreadException cannot:
        // MainForm's constructor, which runs before the message loop starts and is where the
        // database is opened and the background services are wired up, and anything that escapes
        // the loop itself. Both used to end as a bare Windows crash dialog with nothing logged.
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            CrashHandler.ReportStartupFailure(ex);
            Environment.ExitCode = 1;
        }
    }
}