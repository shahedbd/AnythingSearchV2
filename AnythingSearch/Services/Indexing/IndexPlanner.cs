using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// One unit of work for the scanner. A unit is the checkpoint granularity: when it finishes its
/// entries are committed and its key is recorded in the state file, so a resumed run skips it.
/// <see cref="Recursive"/> = false means the directory contributes its own files only - its
/// subdirectories are queued as units of their own, which keeps a subtree from being walked twice.
/// </summary>
internal readonly record struct ScanRoot(DirectoryInfo Directory, bool Recursive)
{
    /// <summary>Stable key used in the state file's checkpoint list.</summary>
    public string Key => (Recursive ? "R|" : "F|") +
                         Directory.FullName.TrimEnd(Path.DirectorySeparatorChar);
}

/// <summary>One scope of the plan: phase 1 as a whole, or a single drive.</summary>
internal sealed record IndexScopeDefinition(string Key, IndexPhase Phase, string Drive, string Label);

/// <summary>
/// Decides what gets indexed, in what order, and in what checkpointable units.
///
/// Phase 1 is Downloads plus the directories holding recently used files, so the app becomes
/// searchable within seconds. Phase 2 is every non-OS fixed drive, one scope per drive so each
/// one publishes as soon as it finishes. Phase 3 is the OS drive, which is both the largest and
/// the least interesting to search, so it goes last.
/// </summary>
internal sealed class IndexPlanner
{
    /// <summary>A top-level directory with more subdirectories than this is split into them.</summary>
    private const int SplitThreshold = 4;

    private readonly SettingsManager _settingsManager;

    public IndexPlanner(SettingsManager settingsManager) => _settingsManager = settingsManager;

    /// <summary>The drive Windows is installed on, e.g. "C:\".</summary>
    public static string OsDrive =>
        Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    public static string DownloadsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>The scopes to run, in order. Keys are stable across runs so progress resumes.</summary>
    public List<IndexScopeDefinition> BuildScopes()
    {
        var scopes = new List<IndexScopeDefinition>
        {
            new("phase1:priority", IndexPhase.Priority, OsDrive, "Downloads and recent files")
        };

        var osDrive = OsDrive;

        foreach (var drive in ReadyFixedDrives())
        {
            if (drive.Name.Equals(osDrive, StringComparison.OrdinalIgnoreCase)) continue;
            scopes.Add(new($"drive:{drive.Name}", IndexPhase.DataDrives, drive.Name, $"Drive {drive.Name}"));
        }

        if (_settingsManager.Settings.IndexSystemDrive)
            scopes.Add(new($"os:{osDrive}", IndexPhase.SystemDrive, osDrive, $"System drive {osDrive}"));

        return scopes;
    }

    private static List<DriveInfo> ReadyFixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<DriveInfo>();
        }
    }

    /// <summary>
    /// Split a scope into units. Units are processed a few at a time and committed at each
    /// boundary, which both caps the size of a transaction and gives resume something to skip.
    /// </summary>
    public List<ScanRoot> ExpandUnits(IndexScopeDefinition scope) => scope.Phase switch
    {
        IndexPhase.Priority => ExpandPriorityUnits(),
        _ => ExpandDriveUnits(scope.Drive)
    };

    /// <summary>
    /// Phase 1: Downloads in full, plus the directory of each recently used file. The recent
    /// directories are deliberately NOT recursive - the point is to make the files the user has
    /// actually been working with searchable immediately, not to walk their whole project trees.
    /// </summary>
    private List<ScanRoot> ExpandPriorityUnits()
    {
        var units = new List<ScanRoot>();

        if (Directory.Exists(DownloadsPath) && !IsExcluded(DownloadsPath))
            units.Add(new ScanRoot(new DirectoryInfo(DownloadsPath), true));

        foreach (var path in RecentItemsLocator.GetRecentDirectories())
        {
            if (IsExcluded(path)) continue;

            // Already covered by the Downloads unit above.
            if (path.StartsWith(DownloadsPath, StringComparison.OrdinalIgnoreCase)) continue;

            units.Add(new ScanRoot(new DirectoryInfo(path), false));
        }

        return Deduplicate(units);
    }

    /// <summary>
    /// A drive: every top-level directory, split one level further when it has enough
    /// subdirectories to be worth checkpointing separately, plus the drive root for the files
    /// sitting directly on it.
    /// </summary>
    private List<ScanRoot> ExpandDriveUnits(string driveName)
    {
        var units = new List<ScanRoot>();

        DirectoryInfo root;
        try { root = new DirectoryInfo(driveName); }
        catch { return units; }

        List<DirectoryInfo> topLevel;
        try
        {
            topLevel = root.GetDirectories()
                .Where(d => !IsExcluded(d.FullName) && !IsSystemHidden(d))
                .ToList();
        }
        catch
        {
            return new List<ScanRoot> { new(root, true) };
        }

        foreach (var directory in topLevel)
            AddSplitUnits(directory, units);

        // The drive root itself: its own files only, since every subdirectory above is its own
        // unit. When the root could not be enumerated it is walked whole instead.
        units.Add(new ScanRoot(root, topLevel.Count == 0));

        return Deduplicate(units);
    }

    /// <summary>
    /// Queue a directory as one unit, or - when it is large enough to be worth finer
    /// checkpoints - as one unit per subdirectory plus a files-only unit for the parent.
    /// </summary>
    private void AddSplitUnits(DirectoryInfo directory, List<ScanRoot> units)
    {
        try
        {
            var subDirectories = directory.GetDirectories();
            if (subDirectories.Length <= SplitThreshold)
            {
                units.Add(new ScanRoot(directory, true));
                return;
            }

            foreach (var subDirectory in subDirectories)
            {
                if (!IsExcluded(subDirectory.FullName) && !IsSystemHidden(subDirectory))
                    units.Add(new ScanRoot(subDirectory, true));
            }

            units.Add(new ScanRoot(directory, false));
        }
        catch
        {
            units.Add(new ScanRoot(directory, true));
        }
    }

    /// <summary>
    /// Drop any unit already covered by a recursive unit, so no subtree is walked twice.
    /// </summary>
    private static List<ScanRoot> Deduplicate(List<ScanRoot> units)
    {
        var recursive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
        {
            if (unit.Recursive)
                recursive.Add(Normalize(unit.Directory.FullName));
        }

        var kept = new List<ScanRoot>(units.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var unit in units)
        {
            var path = Normalize(unit.Directory.FullName);
            if (!seen.Add(path)) continue;
            if (IsInsideRecursiveUnit(path, recursive)) continue;
            kept.Add(unit);
        }

        return kept;
    }

    private static bool IsInsideRecursiveUnit(string path, HashSet<string> recursiveUnits)
    {
        var parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(parent))
        {
            if (recursiveUnits.Contains(Normalize(parent))) return true;
            parent = Path.GetDirectoryName(parent);
        }
        return false;
    }

    private static string Normalize(string path) => path.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Every unit of every scope, used by the startup catch-up pass.</summary>
    public List<ScanRoot> CollectAllUnits()
    {
        var all = new List<ScanRoot>();
        foreach (var scope in BuildScopes())
            all.AddRange(ExpandUnits(scope));

        return Deduplicate(all);
    }

    public bool IsExcluded(string path)
    {
        foreach (var excluded in _settingsManager.Settings.ExcludedFolders)
        {
            if (path.Contains($"\\{excluded}\\", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith($"\\{excluded}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public bool IsExcludedExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return _settingsManager.Settings.ExcludedExtensions.Contains(ext);
    }

    /// <summary>System + hidden directories are Windows plumbing, never user content.</summary>
    public static bool IsSystemHidden(DirectoryInfo directory)
    {
        try
        {
            var attributes = directory.Attributes;
            return (attributes & FileAttributes.System) == FileAttributes.System &&
                   (attributes & FileAttributes.Hidden) == FileAttributes.Hidden;
        }
        catch
        {
            return false;
        }
    }
}
