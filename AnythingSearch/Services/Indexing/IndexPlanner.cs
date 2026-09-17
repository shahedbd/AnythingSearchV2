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

/// <summary>
/// One scope of the plan: phase 1 as a whole, a single data drive, or one sub-phase of the OS
/// drive. <c>Root</c> is null for a whole drive, <see cref="IndexPlanner.RestOfDrive"/>
/// for "everything on the OS drive that no earlier sub-phase covered", and otherwise the single
/// directory the scope covers.
/// </summary>
internal sealed record IndexScopeDefinition(
    string Key, IndexPhase Phase, string Drive, string Label, string? Root = null);

/// <summary>
/// Decides what gets indexed, in what order, and in what checkpointable units.
///
/// Phase 1 is the Downloads folder on its own, so the app becomes searchable within seconds.
/// Phase 2 is every non-OS fixed drive, one scope per drive so each
/// one publishes as soon as it finishes. Phase 3 is the OS drive, which is both the largest and
/// the least interesting to search, so it goes last - and is itself split into sub-phases
/// (see IndexPlanner.SystemDrive.cs) so a 600,000-entry drive publishes in stages.
/// </summary>
internal sealed partial class IndexPlanner
{
    /// <summary>A directory with more subdirectories than this is split into them.</summary>
    private const int SplitThreshold = 4;

    /// <summary>
    /// Levels a directory with only a handful of subdirectories may still be descended, looking
    /// for somewhere worth dividing. A drive whose data sits under one or two enormous folders -
    /// a media library, a course archive - would otherwise be a single unit of a million-plus
    /// entries: no checkpoint inside it, and no sub-phase either.
    /// </summary>
    private const int ExtraSplitDepth = 2;

    /// <summary>
    /// Ceiling on units per scope. Splitting deeper costs a directory read per level while
    /// planning and a path per unit in the state file, so a pathological layout must not be
    /// allowed to turn either into something unbounded.
    /// </summary>
    private const int MaxUnitsPerScope = 2_000;

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
            new("phase1:priority", IndexPhase.Priority, OsDrive, "Downloads")
        };

        var osDrive = OsDrive;

        foreach (var drive in ReadyFixedDrives())
        {
            if (drive.Name.Equals(osDrive, StringComparison.OrdinalIgnoreCase)) continue;
            scopes.Add(new($"drive:{drive.Name}", IndexPhase.DataDrives, drive.Name, $"Drive {drive.Name}"));
        }

        if (_settingsManager.Settings.IndexSystemDrive)
            scopes.AddRange(BuildSystemDriveScopes(osDrive));

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
        IndexPhase.SystemDrive => ExpandSystemDriveUnits(scope),
        _ => ExpandDriveUnits(scope.Drive)
    };

    /// <summary>
    /// Phase 1: the Downloads folder, in full. It is split like any other directory, so a large
    /// Downloads folder still checkpoints as it goes rather than being one all-or-nothing unit.
    /// </summary>
    private List<ScanRoot> ExpandPriorityUnits()
    {
        var units = new List<ScanRoot>();

        if (!Directory.Exists(DownloadsPath) || IsExcluded(DownloadsPath))
            return units;

        AddSplitUnits(new DirectoryInfo(DownloadsPath), units);
        return Deduplicate(units);
    }

    /// <summary>
    /// A drive: every top-level directory, split one level further when it has enough
    /// subdirectories to be worth checkpointing separately, plus the drive root for the files
    /// sitting directly on it.
    /// </summary>
    /// <param name="skipTopLevel">
    /// Normalized top-level directories to leave out, used by the OS drive's final sub-phase so
    /// it covers only what the named sub-phases before it did not.
    /// </param>
    private List<ScanRoot> ExpandDriveUnits(string driveName, ISet<string>? skipTopLevel = null)
    {
        var units = new List<ScanRoot>();

        DirectoryInfo root;
        try { root = new DirectoryInfo(driveName); }
        catch { return units; }

        List<DirectoryInfo> topLevel;
        try
        {
            topLevel = root.GetDirectories()
                .Where(d => !IsExcluded(d.FullName) && !IsSkippable(d) &&
                            (skipTopLevel == null || !skipTopLevel.Contains(Normalize(d.FullName))))
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
    /// Queue a directory as one unit, or - when it is worth finer checkpoints - as one unit per
    /// subdirectory plus a files-only unit for the parent.
    ///
    /// A wide directory is split straight away. A narrow one is descended instead, up to
    /// <see cref="ExtraSplitDepth"/> levels: the unit count is what gives both resume and the
    /// sub-phase threshold something to work with, and "two folders holding a million files
    /// each" is a common way for a data drive to be organised. Because the descent only happens
    /// where the fan-out is small, it cannot multiply out - at most
    /// <see cref="SplitThreshold"/> ^ <see cref="ExtraSplitDepth"/> units per branch.
    /// </summary>
    private void AddSplitUnits(DirectoryInfo directory, List<ScanRoot> units, int depthBudget = ExtraSplitDepth)
    {
        try
        {
            var subDirectories = directory.GetDirectories()
                .Where(d => !IsExcluded(d.FullName) && !IsSkippable(d))
                .ToList();

            if (subDirectories.Count == 0 || units.Count >= MaxUnitsPerScope)
            {
                units.Add(new ScanRoot(directory, true));
                return;
            }

            if (subDirectories.Count > SplitThreshold)
            {
                foreach (var subDirectory in subDirectories)
                    units.Add(new ScanRoot(subDirectory, true));

                units.Add(new ScanRoot(directory, false));
                return;
            }

            if (depthBudget <= 0)
            {
                units.Add(new ScanRoot(directory, true));
                return;
            }

            foreach (var subDirectory in subDirectories)
                AddSplitUnits(subDirectory, units, depthBudget - 1);

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

    /// <summary>
    /// Directories the walk must not enter.
    ///
    /// System + hidden together is Windows plumbing, never user content. Reparse points
    /// (junctions and symlinks) are excluded because they are a second name for a tree that is
    /// already indexed under its real path - following one means the same files appear twice
    /// under different paths, which the unique (FolderId, Name) index cannot catch, and a
    /// self-referencing one means the walk never ends. C:\Users alone ships several
    /// ("All Users", "Default User"), which is why this matters most to the OS-drive sub-phases.
    /// </summary>
    public static bool IsSkippable(DirectoryInfo directory)
    {
        try
        {
            var attributes = directory.Attributes;

            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                return true;

            return (attributes & FileAttributes.System) == FileAttributes.System &&
                   (attributes & FileAttributes.Hidden) == FileAttributes.Hidden;
        }
        catch
        {
            return false;
        }
    }
}
