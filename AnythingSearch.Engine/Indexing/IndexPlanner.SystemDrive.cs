using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Splits phase 3 - the OS drive - into sub-phases, so the largest drive on the machine publishes
/// in stages instead of going dark for its whole walk.
///
/// The order is by how likely the user is to search there, biggest-value first:
///   1. Users             (~450,000 entries on a typical machine)
///   2. Program Files (x86)
///   3. Program Files
///   4. Windows
///   5. everything else on the drive
///
/// The directories are resolved from Windows' own special folders rather than hard-coded names.
/// A machine may be localised, may have Program Files relocated, may be 32-bit-only (where both
/// Program Files folders resolve to the same path), or may put profiles somewhere other than
/// C:\Users - so each candidate is resolved, checked to be a top-level directory of the OS drive,
/// deduplicated, and dropped if it does not exist. Whatever is left becomes a sub-phase, and the
/// final sub-phase always covers the remainder, so nothing on the drive is ever missed regardless
/// of the layout.
/// </summary>
internal sealed partial class IndexPlanner
{
    /// <summary>
    /// Marker used as <see cref="IndexScopeDefinition.Root"/> for the last OS-drive sub-phase:
    /// every top-level directory no named sub-phase claimed, plus the drive's own files.
    /// </summary>
    public const string RestOfDrive = "*rest";

    /// <summary>
    /// Build the OS drive's sub-phases. Keys are derived from the directory name, so they stay
    /// stable across runs - a completed sub-phase is still recognised (and skipped) next startup.
    /// </summary>
    private List<IndexScopeDefinition> BuildSystemDriveScopes(string osDrive)
    {
        var roots = SystemDriveSubPhaseRoots(osDrive);
        var scopes = new List<IndexScopeDefinition>(roots.Count + 1);

        foreach (var root in roots)
        {
            var name = Path.GetFileName(root);
            scopes.Add(new IndexScopeDefinition(
                $"os:{osDrive}:{name}", IndexPhase.SystemDrive, osDrive, root, root));
        }

        scopes.Add(new IndexScopeDefinition(
            $"os:{osDrive}:{RestOfDrive}", IndexPhase.SystemDrive, osDrive,
            $"{osDrive} (remaining folders)", RestOfDrive));

        return scopes;
    }

    /// <summary>
    /// The named sub-phase directories that actually exist on this machine, in indexing order and
    /// normalized (no trailing separator). Empty is a valid answer - the remainder sub-phase then
    /// covers the whole drive on its own.
    /// </summary>
    private List<string> SystemDriveSubPhaseRoots(string osDrive)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in SystemDriveCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            var path = Normalize(candidate);
            if (!IsTopLevelDirectoryOf(path, osDrive)) continue;
            if (IsExcluded(path)) continue;
            if (!Directory.Exists(path)) continue;
            if (!seen.Add(path)) continue;

            ordered.Add(path);
        }

        return ordered;
    }

    /// <summary>
    /// Candidate paths in sub-phase order. Resolved from the OS rather than spelled out, so this
    /// works on a localised install and on a machine where these folders have been moved.
    /// </summary>
    private static IEnumerable<string?> SystemDriveCandidates()
    {
        // The profiles root ("C:\Users") is the parent of this user's profile directory.
        yield return SafePath(() => Path.GetDirectoryName(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

        yield return SafePath(() =>
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        // ProgramW6432 is the 64-bit Program Files even when this process is 32-bit, where
        // SpecialFolder.ProgramFiles is redirected to the x86 folder and would be a duplicate.
        yield return SafePath(() => Environment.GetEnvironmentVariable("ProgramW6432"));
        yield return SafePath(() =>
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

        yield return SafePath(() =>
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    private static string? SafePath(Func<string?> resolve)
    {
        try { return resolve(); }
        catch { return null; }
    }

    /// <summary>
    /// True when <paramref name="path"/> sits directly on <paramref name="osDrive"/>. A sub-phase
    /// has to be a top-level directory: the remainder sub-phase enumerates the drive's top level
    /// and skips these by name, so anything deeper would be indexed twice.
    /// </summary>
    private static bool IsTopLevelDirectoryOf(string path, string osDrive)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            return parent != null &&
                   Normalize(parent).Equals(Normalize(osDrive), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Units for one OS-drive sub-phase: either the single directory it owns, or - for the final
    /// sub-phase - the drive minus every directory the named sub-phases claimed.
    /// </summary>
    private List<ScanRoot> ExpandSystemDriveUnits(IndexScopeDefinition scope)
    {
        if (scope.Root == RestOfDrive || scope.Root == null)
        {
            var claimed = new HashSet<string>(
                SystemDriveSubPhaseRoots(scope.Drive), StringComparer.OrdinalIgnoreCase);

            return ExpandDriveUnits(scope.Drive, claimed);
        }

        DirectoryInfo directory;
        try { directory = new DirectoryInfo(scope.Root); }
        catch { return new List<ScanRoot>(); }

        // The directory can disappear between planning and indexing (an uninstall, a moved
        // profile). Reporting no units lets the sub-phase complete cleanly instead of failing.
        if (!directory.Exists) return new List<ScanRoot>();

        var units = new List<ScanRoot>();
        AddSplitUnits(directory, units);
        return Deduplicate(units);
    }
}
