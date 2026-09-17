using DeviceDataModule;

namespace AnythingSearch.Helper
{
    /// <summary>
    /// Removes the data folders earlier releases used, once the current release has a complete
    /// index of its own.
    ///
    /// This release moved to a versioned data folder (see <see cref="AppConfig.AppDataFolderName"/>)
    /// because the index schema, the indexing state file and the settings file all changed shape
    /// at once. Starting clean is cheaper and safer than migrating three formats, but it would
    /// otherwise leave the old folder - and its multi-hundred-megabyte database - on disk forever.
    ///
    /// Deliberately conservative: it runs only after a full index has been built, never touches
    /// the folder in use, and gives up quietly on anything it cannot delete. A folder still held
    /// by an older copy of the app that is running simply gets removed on a later launch.
    /// </summary>
    internal static class LegacyDataCleanup
    {
        private static bool _done;

        /// <summary>
        /// Delete every folder in <see cref="AppConfig.LegacyAppDataFolderNames"/>. Call this only
        /// once indexing has completed, so nothing is thrown away before its replacement exists.
        /// Safe to call more than once - it runs its work at most once per process.
        /// </summary>
        public static void Run()
        {
            if (_done) return;
            _done = true;

            string current;
            try
            {
                current = Normalize(ApplicationDataManager.Instance.ApplicationDataDirectory);
            }
            catch (Exception ex)
            {
                Logger.Log($"Legacy data cleanup skipped - current data directory unknown: {ex.Message}");
                return;
            }

            foreach (var folderName in AppConfig.LegacyAppDataFolderNames)
                TryDelete(folderName, current);
        }

        private static void TryDelete(string folderName, string currentDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderName)) return;

                var path = Normalize(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    folderName));

                if (!Directory.Exists(path)) return;

                // The guard that matters: a mistake in the legacy list, or a release that reuses
                // an old folder name, must never delete the data the app is using right now.
                if (path.Equals(currentDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"Legacy data cleanup skipped {folderName} - it is the folder in use.");
                    return;
                }

                Directory.Delete(path, recursive: true);
                Logger.Log($"Removed the previous release's data folder: {path}");
            }
            catch (Exception ex)
            {
                // Usually an older copy of the app still holding its database open. Nothing is
                // lost by leaving it: the next launch that completes an index tries again.
                Logger.Log($"Could not remove the previous data folder '{folderName}' " +
                           $"(will retry on a later run): {ex.Message}");
            }
        }

        private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }
}
