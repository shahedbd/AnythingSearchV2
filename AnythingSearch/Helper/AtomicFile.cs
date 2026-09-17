namespace AnythingSearch.Helper
{
    /// <summary>
    /// Crash-safe file writes for every JSON store in the app (settings,
    /// data plan, caches, history). Writing directly with
    /// File.WriteAllText truncates the file first — a crash or power loss
    /// mid-write leaves it half-written, and a half-written JSON file means
    /// "all settings silently reset to defaults" on the next launch.
    ///
    /// Instead: write the full content to a sibling .tmp file, then swap it
    /// into place with File.Replace (atomic on NTFS — the file is never in a
    /// partially-written state, whatever happens).
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string contents)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            SwapInPlace(tmp, path);
        }

        public static void WriteAllBytes(string path, byte[] contents)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, contents);
            SwapInPlace(tmp, path);
        }

        private static void SwapInPlace(string tmp, string path)
        {
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }
    }
}
