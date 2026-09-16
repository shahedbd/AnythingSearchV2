using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Per-change-type database sync logic for FileWatcherService, plus the ignore-list filters
/// shared by the scanner in FileWatcherService.ChangeQueue.cs.
/// </summary>
public partial class FileWatcherService
{
    private async Task HandleCreatedAsync(string path)
    {
        try
        {
            // Check if already exists in database
            if (await _database.ExistsAsync(path))
                return;

            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                await _database.InsertSingleAsync(new FileEntry
                {
                    Name = dir.Name,
                    Path = dir.FullName,
                    Extension = "",
                    Size = 0,
                    Modified = dir.LastWriteTime,
                    IsFolder = true
                });

                // Also index files inside the new directory
                await IndexNewDirectoryAsync(dir);
            }
            else if (File.Exists(path))
            {
                var file = new FileInfo(path);
                if (!IsExcludedExtension(file.Extension))
                {
                    await _database.InsertSingleAsync(new FileEntry
                    {
                        Name = file.Name,
                        Path = file.FullName,
                        Extension = file.Extension.TrimStart('.'),
                        Size = file.Length,
                        Modified = file.LastWriteTime,
                        IsFolder = false
                    });
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Index contents of a newly created directory
    /// </summary>
    private async Task IndexNewDirectoryAsync(DirectoryInfo dir)
    {
        try
        {
            // Index files in this directory
            foreach (var file in dir.EnumerateFiles())
            {
                try
                {
                    if (IsExcludedExtension(file.Extension))
                        continue;

                    await _database.InsertSingleAsync(new FileEntry
                    {
                        Name = file.Name,
                        Path = file.FullName,
                        Extension = file.Extension.TrimStart('.'),
                        Size = file.Length,
                        Modified = file.LastWriteTime,
                        IsFolder = false
                    });
                }
                catch { }
            }

            // Recursively index subdirectories
            foreach (var subDir in dir.EnumerateDirectories())
            {
                try
                {
                    if (ShouldIgnore(subDir.FullName))
                        continue;

                    await _database.InsertSingleAsync(new FileEntry
                    {
                        Name = subDir.Name,
                        Path = subDir.FullName,
                        Extension = "",
                        Size = 0,
                        Modified = subDir.LastWriteTime,
                        IsFolder = true
                    });

                    await IndexNewDirectoryAsync(subDir);
                }
                catch { }
            }
        }
        catch { }
    }

    private async Task HandleDeletedAsync(string path)
    {
        await _database.DeleteByPathAsync(path);
    }

    private async Task HandleRenamedAsync(string oldPath, string newPath)
    {
        await _database.UpdatePathAsync(oldPath, newPath);
    }

    private async Task HandleModifiedAsync(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);

                // Only update if file exists in database
                if (await _database.ExistsAsync(path))
                {
                    await _database.UpdateFileAsync(new FileEntry
                    {
                        Name = file.Name,
                        Path = file.FullName,
                        Extension = file.Extension.TrimStart('.'),
                        Size = file.Length,
                        Modified = file.LastWriteTime,
                        IsFolder = false
                    });
                }
                else
                {
                    // File was created but we missed the create event - add it
                    await HandleCreatedAsync(path);
                }
            }
        }
        catch { }
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

        // Ignore temporary/system files
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName)) return true;

        if (fileName.StartsWith("~$") ||
            fileName.StartsWith(".") ||
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
