using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// The ignore filters the watcher applies, split out of FileWatcherService.SyncHandlers.cs
/// because of where they run: <see cref="ShouldIgnore"/> is called on the raw
/// FileSystemWatcher callback thread, once per OS event, before anything is queued.
///
/// That thread has a hard deadline. Windows hands events to it out of a fixed kernel buffer, and
/// anything still unread when the buffer fills is thrown away and reported as an overflow error -
/// which costs the whole subtree until the next catch-up pass. So the filters must not allocate.
///
/// They used to build two interpolated strings per excluded folder per event: with the ~28
/// entries in <see cref="AppSettings.ExcludedFolders"/> that is 56 string allocations for every
/// file that changes anywhere on the machine, which during a build or a large copy is millions of
/// short-lived strings competing for gen-0 with the search the user is waiting on. The patterns
/// are now built once and reused until the settings object itself is replaced.
/// </summary>
public partial class FileWatcherService
{
    /// <summary>
    /// The settings instance the cached patterns were built from. Volatile, and assigned LAST,
    /// so a reader that sees it also sees the arrays below. SettingsService hands out the same
    /// instance until Reload/ResetToDefaults swaps it, which is what makes this a valid cache key.
    /// </summary>
    private volatile AppSettings? _filterSettings;

    private string[] _excludedSegments = Array.Empty<string>();   // "\name\" - matches mid-path
    private string[] _excludedSuffixes = Array.Empty<string>();   // "\name"  - matches the path itself
    private HashSet<string> _excludedExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _filterLock = new();

    private void EnsureFilters()
    {
        var settings = SettingsService.Current;
        if (ReferenceEquals(_filterSettings, settings)) return;

        lock (_filterLock)
        {
            if (ReferenceEquals(_filterSettings, settings)) return;

            var folders = settings.ExcludedFolders;
            var segments = new string[folders.Count];
            var suffixes = new string[folders.Count];
            for (int i = 0; i < folders.Count; i++)
            {
                segments[i] = $"\\{folders[i]}\\";
                suffixes[i] = $"\\{folders[i]}";
            }

            _excludedSegments = segments;
            _excludedSuffixes = suffixes;
            _excludedExtensions = new HashSet<string>(settings.ExcludedExtensions, StringComparer.OrdinalIgnoreCase);
            _filterSettings = settings;
        }
    }

    private bool ShouldIgnore(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;

        EnsureFilters();

        // Read once into locals: the arrays are swapped as a pair when settings change.
        var segments = _excludedSegments;
        var suffixes = _excludedSuffixes;
        for (int i = 0; i < segments.Length; i++)
        {
            if (path.Contains(segments[i], StringComparison.OrdinalIgnoreCase)) return true;
            if (path.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Ignore temporary/system files.
        // NOTE: names starting with "." are NOT ignored - that used to hide real content such as
        // .github, .vscode, .env from the watcher even though the initial scan indexes them.
        var fileName = Path.GetFileName(path.AsSpan());
        if (fileName.IsEmpty) return true;

        if (fileName.StartsWith("~$", StringComparison.Ordinal) ||
            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Ignore database files
        return fileName.StartsWith("AnythingSearch.db", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsExcludedExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;

        EnsureFilters();
        return _excludedExtensions.Contains(extension.TrimStart('.'));
    }
}
