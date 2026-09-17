using DeviceDataModule;

namespace AnythingSearch.Helper
{
    /// <summary>
    /// The app's file log: %LocalAppData%\…\Logs\app_log.txt.
    ///
    /// Written from the UI thread, the speed tick, the DataUsageMonitor timer
    /// and the startup task, in a process that runs for days — so every append
    /// takes a lock (concurrent appends used to throw IOException straight into
    /// an empty catch, losing the entries that mattered most) and the file is
    /// rolled at <see cref="MaxBytes"/> instead of growing without end.
    /// See Doc/Bug_report/v5.0.4.0_QA_Full_Test_Report.md, M-22.
    /// </summary>
    public static class Logger
    {
        /// <summary>Roll once the live file passes this size.</summary>
        private const long MaxBytes = 1024 * 1024;

        /// <summary>Rolled generations kept beside it (app_log.1.txt, app_log.2.txt).</summary>
        private const int Generations = 2;

        private static readonly object Gate = new();

        private static readonly string LogDirectory = ApplicationDataManager.Instance.LogsDirectory;
        private static readonly string LogPath = Path.Combine(LogDirectory, "app_log.txt");

        static Logger() => PurgeLegacyFiles();

        // ─────────────────────────────────────────────────────────────────────
        // WRITE
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Logs the whole exception — ToString() carries the inner exceptions,
        /// which is the half that explains a WMI/WinRT/HTTP failure and which
        /// the old Message + StackTrace pair threw away.
        /// </summary>
        public static void Log(Exception ex) =>
            Write("ERROR", ex?.ToString() ?? "(null exception)");

        public static void Log(string message) => Write("INFO", message);

        private static void Write(string level, string text)
        {
            try
            {
                string entry =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level}: {text}{Environment.NewLine}";

                // One writer at a time. The lock is per-process, which is all
                // this app needs — it runs single-instance.
                lock (Gate)
                {
                    RollIfOversized();
                    File.AppendAllText(LogPath, entry);
                }
            }
            catch
            {
                // Logging must never take the app down with it.
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ROTATION
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Shifts app_log.txt → app_log.1.txt → app_log.2.txt and drops what
        /// falls off the end, so the Logs folder stays bounded at roughly
        /// (Generations + 1) × MaxBytes. Called under <see cref="Gate"/>.
        /// </summary>
        private static void RollIfOversized()
        {
            var live = new FileInfo(LogPath);

            if (!live.Exists || live.Length < MaxBytes) return;

            for (int generation = Generations; generation >= 1; generation--)
            {
                string source = generation == 1 ? LogPath : GenerationPath(generation - 1);
                string destination = GenerationPath(generation);

                if (!File.Exists(source)) continue;

                // File.Move's overwrite overload replaces the oldest generation
                // in one step, so there is no window with the log missing.
                File.Move(source, destination, overwrite: true);
            }
        }

        private static string GenerationPath(int generation) =>
            Path.Combine(LogDirectory, $"app_log.{generation}.txt");

        // ─────────────────────────────────────────────────────────────────────
        // LEGACY FILES
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Removes exception.log / startup.log — written by an earlier version
        /// that shared this data folder and by nothing in this tree. They are
        /// stale the moment this build runs, and left in place they send anyone
        /// triaging a support report down the wrong file.
        ///
        /// Runs once per process, not once per install: re-checking costs a
        /// File.Exists on start-up and needs no upgrade flag to be kept in sync.
        /// </summary>
        private static void PurgeLegacyFiles()
        {
            foreach (string name in new[] { "exception.log", "startup.log" })
            {
                try
                {
                    string path = Path.Combine(LogDirectory, name);

                    if (File.Exists(path)) File.Delete(path);
                }
                catch
                {
                    // Locked or already gone — either way, not worth a retry.
                }
            }
        }
    }
}
