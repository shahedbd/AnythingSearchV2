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

        /// <summary>Resolved on first write and cached for the life of the process.</summary>
        private static string? _logDirectory;

        /// <summary>
        /// Set while this class is asking ApplicationDataManager where to write.
        ///
        /// ApplicationDataManager logs through Logger, including from inside the very call that
        /// creates the Logs folder ("Created subdirectory: Logs"). That inner call would ask for
        /// the directory again - it is not cached yet - and recurse until the stack ran out. Its
        /// own try/catch cannot help: a StackOverflowException cannot be caught, which is why
        /// this showed up as the process dying rather than as a lost log entry.
        ///
        /// Thread-static, so a genuine log call on another thread is never dropped just because
        /// this one happens to be resolving.
        /// </summary>
        [ThreadStatic]
        private static bool _resolvingDirectory;

        private static bool _purged;

        /// <summary>
        /// The log directory, or null when it cannot be resolved right now - either because
        /// ApplicationDataManager failed, or because the caller IS ApplicationDataManager telling
        /// us about the folder we are in the middle of asking for. Failing leaves the cache empty
        /// so the next write tries again.
        /// </summary>
        private static string? TryGetLogDirectory()
        {
            if (_logDirectory != null) return _logDirectory;
            if (_resolvingDirectory) return null;

            _resolvingDirectory = true;
            try
            {
                return _logDirectory = ApplicationDataManager.Instance.LogsDirectory;
            }
            catch
            {
                return null;
            }
            finally
            {
                _resolvingDirectory = false;
            }
        }

        /// <summary>
        /// Full path of the live log file, or null while the log directory cannot be resolved.
        /// Exposed so <see cref="CrashHandler"/> can tell the user which file to attach to a
        /// support report, rather than composing "app_log.txt" a second time somewhere else.
        /// </summary>
        public static string? LogFilePath
        {
            get
            {
                string? directory = TryGetLogDirectory();
                return directory == null ? null : LivePath(directory);
            }
        }

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
                string? directory = TryGetLogDirectory();
                if (directory == null) return;

                string entry =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level}: {text}{Environment.NewLine}";

                // One writer at a time. The lock is per-process, which is all
                // this app needs — it runs single-instance.
                lock (Gate)
                {
                    if (!_purged)
                    {
                        _purged = true;
                        PurgeLegacyFiles(directory);
                    }

                    RollIfOversized(directory);
                    File.AppendAllText(LivePath(directory), entry);
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
        private static void RollIfOversized(string directory)
        {
            var live = new FileInfo(LivePath(directory));

            if (!live.Exists || live.Length < MaxBytes) return;

            for (int generation = Generations; generation >= 1; generation--)
            {
                string source = generation == 1
                    ? LivePath(directory)
                    : GenerationPath(directory, generation - 1);

                string destination = GenerationPath(directory, generation);

                if (!File.Exists(source)) continue;

                // File.Move's overwrite overload replaces the oldest generation
                // in one step, so there is no window with the log missing.
                File.Move(source, destination, overwrite: true);
            }
        }

        private static string LivePath(string directory) =>
            Path.Combine(directory, "app_log.txt");

        private static string GenerationPath(string directory, int generation) =>
            Path.Combine(directory, $"app_log.{generation}.txt");

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
        private static void PurgeLegacyFiles(string directory)
        {
            foreach (string name in new[] { "exception.log", "startup.log" })
            {
                try
                {
                    string path = Path.Combine(directory, name);

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
