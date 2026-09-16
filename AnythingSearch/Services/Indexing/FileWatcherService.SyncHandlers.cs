using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Per-change-type database sync logic for FileWatcherService, plus the ignore-list filters
/// shared by the scanner in FileWatcherService.ChangeQueue.cs.
///
/// Every handler resolves the path against the real file system before writing. OS events are
/// only a hint that "something happened here" - they arrive out of order, are coalesced by
/// Windows, and are lost when the watcher buffer overflows, so the event type alone must never
/// decide whether a row is inserted or deleted.
/// </summary>
public partial class FileWatcherService
{
    private async Task HandleCreatedAsync(string path)
    {
        if (Directory.Exists(path))
        {
            var dir = new DirectoryInfo(path);

            if (!await _database.ExistsAsync(path))
            {
                var entry = new FileEntry
                {
                    Name = dir.Name,
                    Path = dir.FullName,
                    Extension = "",
                    Size = 0,
                    Modified = dir.LastWriteTime,
                    IsFolder = true
                };
                await _database.InsertSingleAsync(entry);
                _memoryIndex?.NotifyAdded(entry);
            }

            // Also index files inside the new directory
            await IndexNewDirectoryAsync(dir);
        }
        else if (File.Exists(path))
        {
            var file = new FileInfo(path);
            if (IsExcludedExtension(file.Extension)) return;

            if (await _database.ExistsAsync(path)) return;

            var entry = new FileEntry
            {
                Name = file.Name,
                Path = file.FullName,
                Extension = file.Extension.TrimStart('.'),
                Size = file.Length,
                Modified = file.LastWriteTime,
                IsFolder = false
            };
            await _database.InsertSingleAsync(entry);
            _memoryIndex?.NotifyAdded(entry);
        }
    }

    /// <summary>
    /// Index contents of a newly created directory
    /// </summary>
    private async Task IndexNewDirectoryAsync(DirectoryInfo dir)
    {
        foreach (var file in dir.EnumerateFiles())
        {
            try
            {
                if (IsExcludedExtension(file.Extension)) continue;
                if (ShouldIgnore(file.FullName)) continue;
                if (await _database.ExistsAsync(file.FullName)) continue;

                var entry = new FileEntry
                {
                    Name = file.Name,
                    Path = file.FullName,
                    Extension = file.Extension.TrimStart('.'),
                    Size = file.Length,
                    Modified = file.LastWriteTime,
                    IsFolder = false
                };
                await _database.InsertSingleAsync(entry);
                _memoryIndex?.NotifyAdded(entry);
            }
            catch { }
        }

        // Recursively index subdirectories
        foreach (var subDir in dir.EnumerateDirectories())
        {
            try
            {
                if (ShouldIgnore(subDir.FullName)) continue;

                if (!await _database.ExistsAsync(subDir.FullName))
                {
                    var entry = new FileEntry
                    {
                        Name = subDir.Name,
                        Path = subDir.FullName,
                        Extension = "",
                        Size = 0,
                        Modified = subDir.LastWriteTime,
                        IsFolder = true
                    };
                    await _database.InsertSingleAsync(entry);
                    _memoryIndex?.NotifyAdded(entry);
                }

                await IndexNewDirectoryAsync(subDir);
            }
            catch { }
        }
    }

    private async Task HandleDeletedAsync(string path)
    {
        // Atomic saves (write temp -> delete target -> rename) and fast delete/recreate cycles
        // produce a Delete event for a path that exists again by the time we get here.
        if (File.Exists(path) || Directory.Exists(path))
        {
            await HandleCreatedAsync(path);
            return;
        }

        await _database.DeleteByPathAsync(path);
        _memoryIndex?.NotifyRemoved(path);
    }

    private async Task HandleRenamedAsync(string oldPath, string newPath)
    {
        var updated = await _database.UpdatePathAsync(oldPath, newPath);

        // Nothing to rename means the old path was never indexed (created while the app was
        // closed, or lost in a watcher buffer overflow) - index the new path from scratch.
        if (updated == 0)
        {
            await HandleCreatedAsync(newPath);
            return;
        }

        _memoryIndex?.NotifyRemoved(oldPath);
        if (File.Exists(newPath))
        {
            var file = new FileInfo(newPath);
            _memoryIndex?.NotifyAdded(new FileEntry
            {
                Name = file.Name,
                Path = file.FullName,
                Extension = file.Extension.TrimStart('.'),
                Size = file.Length,
                Modified = file.LastWriteTime,
                IsFolder = false
            });
        }
        else if (Directory.Exists(newPath))
        {
            var dir = new DirectoryInfo(newPath);
            _memoryIndex?.NotifyAdded(new FileEntry
            {
                Name = dir.Name,
                Path = dir.FullName,
                Extension = "",
                Size = 0,
                Modified = dir.LastWriteTime,
                IsFolder = true
            });
        }
    }

    private async Task HandleModifiedAsync(string path)
    {
        if (!File.Exists(path))
        {
            // A directory changing its contents raises Modified on the directory itself
            if (Directory.Exists(path)) await HandleCreatedAsync(path);
            return;
        }

        var file = new FileInfo(path);

        // Only update if file exists in database
        if (await _database.ExistsAsync(path))
        {
            var entry = new FileEntry
            {
                Name = file.Name,
                Path = file.FullName,
                Extension = file.Extension.TrimStart('.'),
                Size = file.Length,
                Modified = file.LastWriteTime,
                IsFolder = false
            };
            await _database.UpdateFileAsync(entry);
            _memoryIndex?.NotifyUpdated(entry);
        }
        else
        {
            // File was created but we missed the create event - add it
            await HandleCreatedAsync(path);
        }
    }

    private bool ShouldIgnore(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;

        // Ignore excluded folders
        foreach (var excluded in _settingsManager.Settings.ExcludedFolders)
        {
            if (path.Contains($"\\{excluded}\\", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith($"\\{excluded}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Ignore temporary/system files.
        // NOTE: names starting with "." are NOT ignored - that used to hide real content such as
        // .github, .vscode, .env from the watcher even though the initial scan indexes them.
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName)) return true;

        if (fileName.StartsWith("~$") ||
            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Ignore database files
        if (fileName.StartsWith("AnythingSearch.db", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private bool IsExcludedExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return _settingsManager.Settings.ExcludedExtensions.Contains(ext);
    }
}
