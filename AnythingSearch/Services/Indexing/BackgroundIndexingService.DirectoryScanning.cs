using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Directory-walking producer side of BackgroundIndexingService: decides which directories to
/// scan, splits large ones for parallelism, and walks them into the channel consumed by
/// BackgroundIndexingService.Consumer.cs.
/// </summary>
public partial class BackgroundIndexingService
{
    /// <summary>
    /// One unit of work for the parallel scan. When a directory is split for parallelism the
    /// parent is queued as <see cref="Recursive"/> = false so its subtree is not walked twice -
    /// once by the parent root and once by the child root - which used to insert every item
    /// under a split directory two or more times.
    /// </summary>
    private readonly record struct ScanRoot(DirectoryInfo Directory, bool Recursive);

    /// <summary>
    /// Collect all root directories, splitting large ones for better parallelism
    /// </summary>
    private List<ScanRoot> CollectRootDirectories()
    {
        var result = new List<ScanRoot>();

        var downloadsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        var allDrives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .ToList();

        foreach (var drive in allDrives)
        {
            if (drive.Name.ToUpper() == "C:\\")
            {
                if (Directory.Exists(downloadsPath))
                    result.Insert(0, new ScanRoot(new DirectoryInfo(downloadsPath), true));

                if (!_settingsManager.Settings.IndexSystemDrive)
                    continue;

                AddDriveRoots(drive, result, downloadsPath);
            }
            else
            {
                AddDriveRoots(drive, result, null);
            }
        }

        return result;
    }

    /// <summary>
    /// Queue every top-level directory of a drive (split further when large), plus the drive
    /// root itself for the files that sit directly on it.
    /// </summary>
    private void AddDriveRoots(DriveInfo drive, List<ScanRoot> result, string? skipPath)
    {
        try
        {
            var rootDirs = drive.RootDirectory.GetDirectories()
                .Where(d => !IsExcluded(d.FullName) &&
                            (skipPath == null || !d.FullName.Equals(skipPath, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var rootDir in rootDirs)
            {
                SplitLargeDirectory(rootDir, result);
            }

            // Files directly under the drive root (never covered by the loop above)
            result.Add(new ScanRoot(drive.RootDirectory, rootDirs.Count == 0));
        }
        catch
        {
            result.Add(new ScanRoot(drive.RootDirectory, true));
        }
    }

    /// <summary>
    /// Split large directories into subdirectories for better parallelism
    /// </summary>
    private void SplitLargeDirectory(DirectoryInfo dir, List<ScanRoot> result)
    {
        try
        {
            var subDirs = dir.GetDirectories();
            if (subDirs.Length > 5)
            {
                // Each subdirectory becomes its own recursive root...
                foreach (var subDir in subDirs)
                {
                    if (!IsExcluded(subDir.FullName))
                        result.Add(new ScanRoot(subDir, true));
                }

                // ...so the parent only contributes its own entry and its own files
                result.Add(new ScanRoot(dir, false));
            }
            else
            {
                result.Add(new ScanRoot(dir, true));
            }
        }
        catch
        {
            result.Add(new ScanRoot(dir, true));
        }
    }

    /// <summary>
    /// Ultra-fast directory scanning optimized for throughput
    /// </summary>
    private void ScanDirectoryFast(ScanRoot root, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        var directoryStack = new Stack<DirectoryInfo>(100);
        directoryStack.Push(root.Directory);

        var writer = _channel!.Writer;
        var localFolderCount = 0;

        while (directoryStack.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var currentDir = directoryStack.Pop();

            try
            {
                var fullName = currentDir.FullName;
                if (IsExcluded(fullName)) continue;

                _currentPath = fullName;

                // Add folder entry (not for a drive root - it has no parent folder to live in)
                if (currentDir.Parent != null)
                {
                    var folderEntry = new FileEntry
                    {
                        Name = currentDir.Name,
                        Path = fullName,
                        Extension = "",
                        Size = 0,
                        Modified = currentDir.LastWriteTime,
                        IsFolder = true
                    };

                    while (!writer.TryWrite(folderEntry))
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        Thread.SpinWait(100);
                    }
                    localFolderCount++;
                }

                // Process files
                try
                {
                    foreach (var file in currentDir.EnumerateFiles())
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        try
                        {
                            var ext = file.Extension;
                            if (IsExcludedExtension(ext)) continue;

                            var fileEntry = new FileEntry
                            {
                                Name = file.Name,
                                Path = file.FullName,
                                Extension = ext.Length > 0 ? ext.Substring(1) : "",
                                Size = file.Length,
                                Modified = file.LastWriteTime,
                                IsFolder = false
                            };

                            while (!writer.TryWrite(fileEntry))
                            {
                                if (cancellationToken.IsCancellationRequested) return;
                                Thread.SpinWait(100);
                            }

                            var total = Interlocked.Increment(ref _totalFiles);
                            if (total % ProgressReportInterval == 0)
                                ReportProgress();
                        }
                        catch { }
                    }
                }
                catch { }

                // Add subdirectories (skipped for a files-only root - they are scanned by
                // their own ScanRoot entry)
                if (!root.Recursive) continue;

                try
                {
                    foreach (var subDir in currentDir.EnumerateDirectories())
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        try
                        {
                            var attrs = subDir.Attributes;
                            if ((attrs & FileAttributes.System) == FileAttributes.System &&
                                (attrs & FileAttributes.Hidden) == FileAttributes.Hidden)
                                continue;

                            if (!IsExcluded(subDir.FullName))
                                directoryStack.Push(subDir);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch { }
        }

        Interlocked.Add(ref _totalFolders, localFolderCount);
    }

    private bool IsExcluded(string path)
    {
        foreach (var excluded in _settingsManager.Settings.ExcludedFolders)
        {
            if (path.Contains($"\\{excluded}\\", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith($"\\{excluded}", StringComparison.OrdinalIgnoreCase))
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
