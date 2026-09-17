using DeviceDataModule;

namespace AnythingSearch.Helper
{
    /// <summary>
    /// The app's file log: %LocalAppData%\…\Logs\app_log.txt.
    ///
    /// Written from the UI thread, the indexing pipeline's walker and writer threads, the file
    /// watcher's timer and the startup task, in a process that can sit in the tray for days — so
    /// every append takes a lock (concurrent appends used to throw IOException straight into an
    /// empty catch, losing the entries that mattered most) and the file is rolled at
    /// <see cref="MaxBytes"/> instead of growing without end.
    ///
    /// Use this rather than Debug.WriteLine: Debug output is compiled out of a Release build, so
    /// anything logged that way is invisible in the one place it matters, a user's support report.
    /// </summary>
    public static class Logger
    {
        /// <summary>Roll once the live file passes this size.</summary>
        private const long MaxBytes = 1024 * 1024;

        /// <summary>Rolled generations kept beside it (app_log.1.txt, app_log.2.txt).</summary>
        private const int Generations = 2;

        private static readonly object Gate = new();

        /// <summary>
        /// Resolved on first write, not at type load. ApplicationDataManager logs through this
        /// class while it is bootstrapping, and asking it for a directory from a static field
        /// initializer made the two types initialize each other - which its Lazy instance
        /// rejects. A failed attempt leaves this null, so the next write simply tries again.
        /// </summary>
        private static string? _logDirectory;

        private static bool _purged;

        private static string LogDirectory =>
            _logDirectory ??= ApplicationDataManager.Instance.LogsDirectory;

        private static string LogPath => Path.Combine(LogDirectory, "app_log.txt");

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
                    if (!_purged)
                    {
                        _purged = true;
                        PurgeLegacyFiles();
                    }

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
        /// Runs once per process on the first write, not once per install: re-checking costs
        /// a File.Exists on start-up and needs no upgrade flag to be kept in sync.
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
