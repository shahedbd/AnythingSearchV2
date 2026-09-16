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
    /// Collect all root directories, splitting large ones for better parallelism
    /// </summary>
    private List<DirectoryInfo> CollectRootDirectories()
    {
        var result = new List<DirectoryInfo>();

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
                    result.Insert(0, new DirectoryInfo(downloadsPath));

                if (_settingsManager.Settings.IndexSystemDrive)
                {
                    try
                    {
                        var rootDirs = drive.RootDirectory.GetDirectories()
                            .Where(d => !IsExcluded(d.FullName) &&
                                       !d.FullName.Equals(downloadsPath, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var rootDir in rootDirs)
                        {
                            SplitLargeDirectory(rootDir, result);
                        }
                    }
                    catch { }
                }
            }
            else
            {
                try
                {
                    var rootDirs = drive.RootDirectory.GetDirectories()
                        .Where(d => !IsExcluded(d.FullName))
                        .ToList();

                    if (rootDirs.Count > 0)
                    {
                        foreach (var rootDir in rootDirs)
                        {
                            SplitLargeDirectory(rootDir, result);
                        }
                    }
                    else
                    {
                        result.Add(drive.RootDirectory);
                    }
                }
                catch
                {
                    result.Add(drive.RootDirectory);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Split large directories into subdirectories for better parallelism
    /// </summary>
    private void SplitLargeDirectory(DirectoryInfo dir, List<DirectoryInfo> result)
    {
        try
        {
            var subDirs = dir.GetDirectories();
            if (subDirs.Length > 5)
            {
                // Add subdirectories for parallel processing
                result.AddRange(subDirs.Where(d => !IsExcluded(d.FullName)));
                // Also add parent to process its files
                result.Add(dir);
            }
            else
            {
                result.Add(dir);
            }
        }
        catch
        {
            result.Add(dir);
        }
    }

    /// <summary>
    /// Ultra-fast directory scanning optimized for throughput
    /// </summary>
    private void ScanDirectoryFast(DirectoryInfo root, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        var directoryStack = new Stack<DirectoryInfo>(100);
        directoryStack.Push(root);

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

                // Add folder entry
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

                // Add subdirectories
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
