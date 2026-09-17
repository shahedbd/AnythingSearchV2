using System.Text;

namespace AnythingSearch.Services;

/// <summary>
/// Finds the directories holding the user's recently used files, for phase 1 of indexing.
///
/// Windows records every recently opened item as a shortcut in %AppData%\Microsoft\Windows\Recent.
/// The target path is read straight out of the shortcut's LinkInfo structure (MS-SHLLINK) rather
/// than through the shell COM API, so this stays a plain file read with no COM dependency and no
/// risk of blocking on a slow shell extension during startup.
///
/// Anything that cannot be parsed is simply skipped: phase 1 is a convenience pass, and the full
/// drive phases will pick the file up regardless.
/// </summary>
internal static class RecentItemsLocator
{
    /// <summary>Newest shortcuts to look at. Recent holds a few hundred at most.</summary>
    private const int MaxShortcuts = 300;

    /// <summary>Upper bound on directories returned, so phase 1 always stays short.</summary>
    private const int MaxDirectories = 60;

    /// <summary>
    /// Directories containing recently used files, newest first. Drive roots are excluded -
    /// scanning one would turn phase 1 into a whole-drive pass.
    /// </summary>
    public static List<string> GetRecentDirectories()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var recentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrEmpty(recentFolder) || !Directory.Exists(recentFolder))
            return result;

        List<FileInfo> shortcuts;
        try
        {
            shortcuts = new DirectoryInfo(recentFolder)
                .EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxShortcuts)
                .ToList();
        }
        catch
        {
            return result;
        }

        foreach (var shortcut in shortcuts)
        {
            if (result.Count >= MaxDirectories) break;

            var target = TryReadTarget(shortcut.FullName);
            if (string.IsNullOrEmpty(target)) continue;

            // The target may be a file or a directory; either way its own directory is what is
            // worth indexing, because that is where the user's other related files live.
            var directory = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) continue;

            var normalized = directory.TrimEnd(Path.DirectorySeparatorChar);

            // A drive root ("C:") has no parent - indexing it recursively is phase 2/3's job.
            if (normalized.Length <= 2) continue;

            if (seen.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }

    /// <summary>
    /// Read the local target path out of a .lnk file. Returns null when the shortcut has no
    /// local path (a network or virtual target) or cannot be parsed.
    /// </summary>
    private static string? TryReadTarget(string linkPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(linkPath);
            if (bytes.Length < 0x4C) return null;

            var flags = BitConverter.ToUInt32(bytes, 20);
            const uint HasLinkTargetIdList = 0x01;
            const uint HasLinkInfo = 0x02;

            if ((flags & HasLinkInfo) == 0) return null;

            int offset = 0x4C;
            if ((flags & HasLinkTargetIdList) != 0)
            {
                if (offset + 2 > bytes.Length) return null;
                offset += 2 + BitConverter.ToUInt16(bytes, offset);
            }

            return ReadLinkInfoPath(bytes, offset);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse the LinkInfo structure: LocalBasePath (+ CommonPathSuffix) is the full target path.
    /// The Unicode variants are preferred when the header is long enough to declare them.
    /// </summary>
    private static string? ReadLinkInfoPath(byte[] bytes, int start)
    {
        if (start + 0x20 > bytes.Length) return null;

        var headerSize = BitConverter.ToUInt32(bytes, start + 4);
        var linkInfoFlags = BitConverter.ToUInt32(bytes, start + 8);
        const uint VolumeIdAndLocalBasePath = 0x01;
        if ((linkInfoFlags & VolumeIdAndLocalBasePath) == 0) return null;

        var basePathOffset = BitConverter.ToUInt32(bytes, start + 16);
        var suffixOffset = BitConverter.ToUInt32(bytes, start + 24);
        bool unicode = headerSize >= 0x24 && start + 0x24 <= bytes.Length;

        if (unicode)
        {
            var basePathUnicode = BitConverter.ToUInt32(bytes, start + 28);
            var suffixUnicode = BitConverter.ToUInt32(bytes, start + 32);
            if (basePathUnicode != 0)
            {
                var basePath = ReadString(bytes, start + (int)basePathUnicode, Encoding.Unicode);
                var suffix = suffixUnicode == 0
                    ? ""
                    : ReadString(bytes, start + (int)suffixUnicode, Encoding.Unicode);
                return Combine(basePath, suffix);
            }
        }

        if (basePathOffset == 0) return null;

        var ansi = Encoding.Default;
        return Combine(
            ReadString(bytes, start + (int)basePathOffset, ansi),
            suffixOffset == 0 ? "" : ReadString(bytes, start + (int)suffixOffset, ansi));
    }

    private static string? Combine(string? basePath, string? suffix)
    {
        if (string.IsNullOrEmpty(basePath)) return null;
        return string.IsNullOrEmpty(suffix) ? basePath : basePath + suffix;
    }

    /// <summary>Null-terminated string at <paramref name="offset"/>, or null if out of range.</summary>
    private static string? ReadString(byte[] bytes, int offset, Encoding encoding)
    {
        if (offset < 0 || offset >= bytes.Length) return null;

        int step = encoding.Equals(Encoding.Unicode) ? 2 : 1;
        int end = offset;

        while (end + step <= bytes.Length)
        {
            bool isTerminator = step == 1
                ? bytes[end] == 0
                : bytes[end] == 0 && bytes[end + 1] == 0;

            if (isTerminator) break;
            end += step;
        }

        return end == offset ? null : encoding.GetString(bytes, offset, end - offset);
    }
}
