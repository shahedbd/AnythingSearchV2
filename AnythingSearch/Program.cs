using AnythingSearch.Helper;
using DeviceDataModule;

namespace AnythingSearch.Forms;

internal static class Program
{
    private static readonly string MutexName = $@"Local\{AppConfig.AppDataFolderName}.Instance";
    private static Mutex _singleInstanceMutex;
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

        // Before the instance check, not after: the "already running"
        // dialog is UI too, and without these it renders with unthemed
        // classic controls.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show($"{AppConfig.AppName} is already running.", AppConfig.AppName);
            _singleInstanceMutex.Dispose();
            return;
        }

        // Enable high DPI support for Windows 10/11
        // This is CRITICAL for Microsoft Store approval at 150% scaling
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Set default font for the entire application (DPI-aware)
        Application.SetDefaultFont(new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point));

        // Run the main form. The guard covers the two windows Application.ThreadException cannot:
        // MainForm's constructor, which runs before the message loop starts and is where the
        // database is opened and the background services are wired up, and anything that escapes
        // the loop itself. Both used to end as a bare Windows crash dialog with nothing logged.
        try
        {
            _ = Task.Run(() => AppStartupService.ExecuteStartupTaskAsync());
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            CrashHandler.ReportStartupFailure(ex);
            Environment.ExitCode = 1;
        }
    }
}