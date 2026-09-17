namespace AnythingSearch.Helper
{
    /// <summary>
    /// The process-wide exception net. Installed as the first thing <see cref="AnythingSearch.Forms.Program"/>
    /// does, before the single-instance check and before any window exists.
    ///
    /// Without it nothing in this app records a crash. A fault on the UI thread raised the raw
    /// .NET exception dialog — a stack trace and a Quit button, in front of a user who cannot act
    /// on either — and a fault on one of the background threads (the indexing pipeline, the file
    /// watcher's timer, the snapshot rebuild, the startup task) killed the process outright. The
    /// app has kept a file log all along (<see cref="Logger"/>); it simply never saw the one class
    /// of event that matters most in a support report. For something that sits in the tray for
    /// days doing continuous background work, that was the largest blind spot in the product.
    ///
    /// Three sources, three different obligations:
    ///
    /// * <see cref="Application.ThreadException"/> — a fault inside a message-loop callback. The
    ///   message loop survives it, so this logs and tells the user in plain language, then lets
    ///   the app carry on.
    /// * <see cref="AppDomain.UnhandledException"/> — the runtime is already tearing the process
    ///   down and this handler cannot stop it. Its only job is to get the exception onto disk
    ///   first, which is why <see cref="Logger"/>'s synchronous append matters here.
    /// * <see cref="TaskScheduler.UnobservedTaskException"/> — a faulted task nobody awaited.
    ///   Harmless to the process since .NET 4.5, and therefore invisible: exactly the shape of
    ///   the fire-and-forget work this app starts for index catch-up and legacy cleanup.
    /// </summary>
    public static class CrashHandler
    {
        /// <summary>Guards against a second Install() re-subscribing the same handlers.</summary>
        private static int _installed;

        /// <summary>
        /// Set while a UI-thread fault is being reported. A MessageBox pumps messages, so a
        /// second fault can arrive while the first dialog is still open - without this the app
        /// would stack dialogs on top of each other and the user could never get back to it.
        /// </summary>
        private static bool _reporting;

        /// <summary>
        /// Shortest gap between two dialogs. A fault in a paint or timer handler repeats every
        /// frame or tick; the log still records every occurrence, but the user is told once.
        /// </summary>
        private static readonly TimeSpan DialogCooldown = TimeSpan.FromSeconds(30);

        private static DateTime _lastDialogUtc = DateTime.MinValue;

        /// <summary>
        /// Subscribe the handlers. Safe to call more than once; only the first call takes effect.
        /// Must run before the first window is created, because
        /// <see cref="Application.SetUnhandledExceptionMode"/> only applies to threads that start
        /// pumping messages after it is set.
        /// </summary>
        public static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1) return;

            try
            {
                // Without this WinForms keeps its default behaviour and shows its own dialog
                // instead of raising ThreadException, so the handler below would never run.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            }
            catch (InvalidOperationException)
            {
                // Thrown only if a message loop is already running on this thread. Nothing to do
                // except leave the default behaviour in place - the other two handlers still work.
            }

            Application.ThreadException += OnUiThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        // ─────────────────────────────────────────────────────────────────────
        // HANDLERS
        // ─────────────────────────────────────────────────────────────────────

        private static void OnUiThreadException(object? sender, ThreadExceptionEventArgs e)
        {
            Logger.Log("Unhandled exception on the UI thread:");
            Logger.Log(e.Exception);

            // Already on the UI thread here, so the dialog can be shown directly.
            ShowFailureNotice();
        }

        private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            // Everything in here has to finish before the process goes away, so there is no
            // dialog and no asynchrony - just the write. Logger swallows its own failures, so a
            // broken log path cannot turn this into a second exception during teardown.
            Logger.Log($"FATAL unhandled exception (terminating: {e.IsTerminating}):");

            if (e.ExceptionObject is Exception exception)
                Logger.Log(exception);
            else
                Logger.Log($"Non-exception object thrown: {e.ExceptionObject?.ToString() ?? "(null)"}");
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Logger.Log("Unobserved exception from a background task:");
            Logger.Log(e.Exception);

            // Mark it handled. It would not take the process down either way, but observing it
            // keeps the runtime from escalating under a ThrowUnobservedTaskExceptions policy and
            // makes the intent explicit: this has been logged, it is not being ignored.
            e.SetObserved();
        }

        /// <summary>
        /// Report a fault that happened while starting up or that escaped the message loop, i.e.
        /// one the app cannot continue past.
        ///
        /// This exists because <see cref="Application.ThreadException"/> only covers exceptions
        /// raised while messages are being pumped. <c>new MainForm()</c> is evaluated before
        /// <see cref="Application.Run"/> is entered, and that constructor opens the index
        /// database and starts the background services - the most likely place in the whole app
        /// for startup to fail. Left alone it reached AppDomain.UnhandledException, which can log
        /// but cannot explain itself to the user or exit tidily.
        /// </summary>
        public static void ReportStartupFailure(Exception exception)
        {
            Logger.Log("Fatal exception during startup - the app cannot continue:");
            Logger.Log(exception);

            try
            {
                MessageBox.Show(
                    $"{AppConfig.AppName} could not start." + Environment.NewLine + Environment.NewLine +
                    DetailsLine(),
                    AppConfig.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not show the startup failure notice: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // USER NOTICE
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One plain-language dialog. No stack trace, no exception type - neither helps the person
        /// reading it. What does help is knowing the app is still usable and which file to send.
        /// </summary>
        private static void ShowFailureNotice()
        {
            if (_reporting) return;
            if (DateTime.UtcNow - _lastDialogUtc < DialogCooldown) return;

            _reporting = true;
            try
            {
                _lastDialogUtc = DateTime.UtcNow;

                var message =
                    $"{AppConfig.AppName} ran into an unexpected problem, so the last thing you " +
                    "did may not have finished." + Environment.NewLine + Environment.NewLine +
                    "The app is still running and you can carry on using it. Searching and " +
                    "indexing are unaffected." + Environment.NewLine + Environment.NewLine +
                    DetailsLine();

                MessageBox.Show(
                    message,
                    AppConfig.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                // A dialog that cannot be shown must not become the next unhandled exception.
                Logger.Log($"Could not show the failure notice: {ex.Message}");
            }
            finally
            {
                _reporting = false;
            }
        }

        /// <summary>
        /// Where to find the details, when there is somewhere to point at. The log path can be
        /// unavailable - that is the one case ApplicationDataManager's fallback chain cannot
        /// recover from - so the message degrades to the support address rather than naming a
        /// file that does not exist.
        /// </summary>
        private static string DetailsLine()
        {
            var logPath = Logger.LogFilePath;

            return logPath == null
                ? $"If it keeps happening, please contact {AppConfig.SupportEmail}."
                : "If it keeps happening, please send this file to " +
                  $"{AppConfig.SupportEmail}:" + Environment.NewLine + logPath;
        }
    }
}
