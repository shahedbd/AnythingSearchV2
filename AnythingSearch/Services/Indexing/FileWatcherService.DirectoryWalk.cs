using AnythingSearch.Helper;
using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// The directory walk the file watcher runs when a new folder appears, split out of
/// FileWatcherService.SyncHandlers.cs so the per-change handlers there stay readable.
///
/// This is the incremental counterpart to the bulk walk in
/// BackgroundIndexingService.DirectoryScanning.cs. The two are deliberately separate - the bulk
/// one streams into a channel for a batched writer, this one writes single entries and mirrors
/// each into the in-memory overlay - but they must agree on WHICH directories belong in the
/// index, which is why both go through <see cref="IndexPlanner.IsSkippable"/>.
/// </summary>
public partial class FileWatcherService
{
    /// <summary>
    /// Deepest level below a newly created directory this will walk.
    ///
    /// A backstop, not the primary guard - <see cref="IndexPlanner.IsSkippable"/> is what stops
    /// a junction loop. This catches the case it cannot see: a redirector or filesystem that
    /// loops without reporting FileAttributes.ReparsePoint. Real trees do not come close to it.
    /// </summary>
    private const int MaxWatchIndexDepth = 64;

    /// <summary>
    /// Index the contents of a newly created directory.
    ///
    /// Walked with an explicit stack rather than by recursion. This used to call itself for every
    /// subdirectory with no guard of any kind, which had two consequences. A directory junction
    /// pointing at one of its own ancestors - <c>mklink /J</c> makes one in seconds, and
    /// C:\Users ships several - never terminated; being async recursion, each level cost a frame
    /// plus a state machine, so it ended in StackOverflowException, which cannot be caught and
    /// takes the process with it. Short of a loop, following any junction re-indexed a tree that
    /// is already indexed under its real path, inflating the database and the in-memory overlay
    /// with duplicates the unique (FolderId, Name) index cannot catch, because the paths differ.
    ///
    /// <see cref="IndexPlanner.IsSkippable"/> is the same guard the bulk indexer applies in
    /// BackgroundIndexingService.DirectoryScanning.cs, so the watcher and a full rebuild now
    /// agree on what belongs in the index.
    /// </summary>
    private async Task IndexNewDirectoryAsync(DirectoryInfo root)
    {
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            // A batch can be draining while the app is closing; a large new folder should not
            // hold that up (see FileWatcherService.StopAsync).
            if (_stopping) return;

            var (directory, depth) = pending.Pop();

            await IndexFilesInAsync(directory);

            if (depth >= MaxWatchIndexDepth)
            {
                Logger.Log($"Auto-watch stopped at depth {MaxWatchIndexDepth}: {directory.FullName}");
                continue;
            }

            await PushSubdirectoriesAsync(directory, depth, pending);
        }
    }

    /// <summary>Index the files sitting directly inside one directory.</summary>
    private async Task IndexFilesInAsync(DirectoryInfo directory)
    {
        try
        {
            foreach (var file in directory.EnumerateFiles())
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
        }
        catch
        {
            // Enumeration itself can fail part way through - access denied, or the directory
            // disappearing mid-walk. Give up on this directory only; the rest of the walk stands.
        }
    }

    /// <summary>
    /// Record each eligible subdirectory and queue it for the walk. Junctions, symlinks and
    /// system+hidden directories are dropped here, which is what bounds the walk.
    /// </summary>
    private async Task PushSubdirectoriesAsync(
        DirectoryInfo directory, int depth, Stack<(DirectoryInfo Directory, int Depth)> pending)
    {
        try
        {
            foreach (var subDirectory in directory.EnumerateDirectories())
            {
                try
                {
                    if (IndexPlanner.IsSkippable(subDirectory)) continue;
                    if (ShouldIgnore(subDirectory.FullName)) continue;

                    if (!await _database.ExistsAsync(subDirectory.FullName))
                    {
                        var entry = new FileEntry
                        {
                            Name = subDirectory.Name,
                            Path = subDirectory.FullName,
                            Extension = "",
                            Size = 0,
                            Modified = subDirectory.LastWriteTime,
                            IsFolder = true
                        };
                        await _database.InsertSingleAsync(entry);
                        _memoryIndex?.NotifyAdded(entry);
                    }

                    pending.Push((subDirectory, depth + 1));
                }
                catch { }
            }
        }
        catch
        {
            // As above: an unreadable directory ends this branch, not the walk.
        }
    }
}
