using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Catch-up pass for an index that is already built.
///
/// FileSystemWatcher only reports changes while the app is running, and Windows drops events
/// when the watcher buffer overflows - so anything created, renamed or deleted while the app was
/// closed is invisible to the index until the next full rebuild. This pass reconciles the index
/// with the disk without rebuilding it: a directory's LastWriteTime changes whenever an entry is
/// added or removed inside it, so only directories whose timestamp no longer matches the indexed
/// value are re-read.
/// </summary>
public partial class BackgroundIndexingService
{
    private volatile bool _isCatchingUp = false;

    /// <summary>Fired with a short progress/summary message while the catch-up runs.</summary>
    public event Action<string>? CatchUpStatusChanged;

    public bool IsCatchingUp => _isCatchingUp;

    /// <summary>
    /// Reconcile the existing index with the current state of the disk.
    /// Safe to call at startup - it does nothing while a full index build is running.
    /// </summary>
    public async Task RunCatchUpAsync(CancellationToken cancellationToken = default)
    {
        if (_isCatchingUp || _isIndexing || !_status.IsReady) return;

        _isCatchingUp = true;
        var added = 0;
        var removed = 0;

        try
        {
            CatchUpStatusChanged?.Invoke("Checking for changes made while the app was closed...");

            var indexedFolders = await _database.GetFolderModifiedMapAsync(cancellationToken);

            var roots = CollectRootDirectories();
            var done = 0;

            foreach (var root in roots)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    // One unreadable root must never abort the whole pass
                    var (a, r) = await CatchUpRootAsync(root, indexedFolders, cancellationToken);
                    added += a;
                    removed += r;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CatchUp] {root.Directory.FullName}: {ex.Message}");
                }

                done++;
                if (done % 20 == 0 || added + removed > 0)
                    CatchUpStatusChanged?.Invoke($"Checking for changes ({done}/{roots.Count}) - {added:N0} added, {removed:N0} removed");
            }

            CatchUpStatusChanged?.Invoke(added == 0 && removed == 0
                ? "Index is up to date"
                : $"Index updated: {added:N0} added, {removed:N0} removed");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CatchUpStatusChanged?.Invoke($"Catch-up failed: {ex.Message}");
        }
        finally
        {
            _isCatchingUp = false;
        }
    }

    /// <summary>
    /// Reconcile a single directory tree instead of every fixed drive.
    /// Test seam for IndexCatchUpTests - production code calls <see cref="RunCatchUpAsync"/>.
    /// </summary>
    internal async Task<(int Added, int Removed)> RunCatchUpForRootAsync(
        string rootPath, CancellationToken cancellationToken = default)
    {
        var indexedFolders = await _database.GetFolderModifiedMapAsync(cancellationToken);
        var root = new ScanRoot(new DirectoryInfo(rootPath), true);
        return await CatchUpRootAsync(root, indexedFolders, cancellationToken);
    }

    /// <summary>
    /// Walk one scan root, re-reading only the directories whose timestamp changed.
    /// </summary>
    private async Task<(int Added, int Removed)> CatchUpRootAsync(
        ScanRoot root, Dictionary<string, long> indexedFolders, CancellationToken cancellationToken)
    {
        var added = 0;
        var removed = 0;

        var stack = new Stack<DirectoryInfo>();
        stack.Push(root.Directory);

        while (stack.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var dir = stack.Pop();
            var path = dir.FullName;
            if (IsExcluded(path)) continue;

            try
            {
                var isDriveRoot = dir.Parent == null;
                var known = indexedFolders.TryGetValue(path, out var indexedTicks);

                // A drive root has no folder row of its own, so always check its files
                if (isDriveRoot || !known || indexedTicks != dir.LastWriteTime.Ticks)
                {
                    if (!known && !isDriveRoot)
                    {
                        await _database.InsertSingleAsync(new FileEntry
                        {
                            Name = dir.Name,
                            Path = path,
                            Extension = "",
                            Size = 0,
                            Modified = dir.LastWriteTime,
                            IsFolder = true
                        });
                        added++;
                    }

                    var (a, r) = await SyncDirectoryAsync(dir, cancellationToken);
                    added += a;
                    removed += r;

                    if (!isDriveRoot)
                        await _database.UpdateFolderModifiedAsync(path, dir.LastWriteTime);
                }

                if (!root.Recursive) continue;

                foreach (var subDir in dir.EnumerateDirectories())
                {
                    try
                    {
                        var attrs = subDir.Attributes;
                        if ((attrs & FileAttributes.System) == FileAttributes.System &&
                            (attrs & FileAttributes.Hidden) == FileAttributes.Hidden)
                            continue;

                        if (!IsExcluded(subDir.FullName))
                            stack.Push(subDir);
                    }
                    catch { }
                }
            }
            catch { }
        }

        return (added, removed);
    }

    /// <summary>
    /// Compare the direct children of one directory against the index: insert what is new,
    /// delete what is gone. Nothing is deleted unless the directory could be fully enumerated.
    /// </summary>
    private async Task<(int Added, int Removed)> SyncDirectoryAsync(
        DirectoryInfo dir, CancellationToken cancellationToken)
    {
        var added = 0;
        var removed = 0;

        List<FileInfo> files;
        List<DirectoryInfo> subDirs;
        try
        {
            files = dir.EnumerateFiles().ToList();
            subDirs = dir.EnumerateDirectories().ToList();
        }
        catch
        {
            // Access denied / disappeared - never treat this as "everything was deleted"
            return (0, 0);
        }

        var indexedNames = await _database.GetChildNamesAsync(dir.FullName, cancellationToken);
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (IsExcludedExtension(file.Extension)) continue;
            onDisk.Add(file.Name);

            if (indexedNames.Contains(file.Name)) continue;

            try
            {
                await _database.InsertSingleAsync(new FileEntry
                {
                    Name = file.Name,
                    Path = file.FullName,
                    Extension = file.Extension.Length > 0 ? file.Extension.Substring(1) : "",
                    Size = file.Length,
                    Modified = file.LastWriteTime,
                    IsFolder = false
                });
                added++;
            }
            catch { }
        }

        foreach (var subDir in subDirs)
        {
            if (IsExcluded(subDir.FullName)) continue;
            onDisk.Add(subDir.Name);
            // The folder row itself is inserted when the walk reaches it
        }

        foreach (var staleName in indexedNames)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (onDisk.Contains(staleName)) continue;

            try
            {
                await _database.DeleteByPathAsync(Path.Combine(dir.FullName, staleName));
                removed++;
            }
            catch { }
        }

        return (added, removed);
    }
}
