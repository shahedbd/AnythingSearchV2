using AnythingSearch.Helper;
using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// The directory walk the file watcher runs when a genuinely new folder appears, split out of
/// FileWatcherService.SyncHandlers.cs so the per-change handlers there stay readable.
///
/// This is the incremental counterpart to the bulk walk in
/// BackgroundIndexingService.DirectoryScanning.cs. The two are deliberately separate - the bulk
/// one streams into a channel for a batched writer, this one writes single entries and mirrors
/// each into the in-memory overlay - but they must agree on WHICH directories belong in the
/// index, which is why both go through <see cref="IndexPlanner.IsSkippable"/>.
///
/// Everything here runs on a work budget. A change notification must cost a bounded amount of
/// database time no matter what arrived, because the connection it writes through is the same one
/// the search box falls back to, and the entries it produces are the same overlay every search
/// scans. Whatever does not fit in one batch is deferred to the next, so a 400,000 file folder
/// being restored is absorbed over a few minutes instead of holding search up for the duration.
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
    /// Entries one batch may index from new directories before the remainder is deferred.
    /// Sized so the walk stays a fraction of the 3 second batch interval on a normal disk.
    /// </summary>
    private const int WalkBudgetPerBatch = 4000;

    /// <summary>
    /// Cap on the deferred backlog. Past this the rest of the tree is left to the next catch-up
    /// pass rather than remembered forever - the database is the durable copy either way.
    /// </summary>
    private const int MaxDeferredDirectories = 20_000;

    /// <summary>
    /// Directories a previous batch ran out of budget on, drained by later batches. Only ever
    /// touched from inside a claimed batch (see ProcessChangesAsync), so it needs no lock.
    /// </summary>
    private readonly Queue<(string Path, int Depth)> _deferredDirectories = new();

    /// <summary>Entries the current batch may still index. Reset by ProcessChangesAsync.</summary>
    private int _walkBudget;

    private void ResetWalkBudget() => _walkBudget = WalkBudgetPerBatch;

    /// <summary>
    /// Pick up where the previous batch ran out of budget. Runs before the batch's own changes so
    /// a backlog always drains, rather than being pushed behind whatever arrived since.
    /// </summary>
    private async Task DrainDeferredDirectoriesAsync()
    {
        while (_deferredDirectories.Count > 0 && _walkBudget > 0 && !_stopping)
        {
            var (path, depth) = _deferredDirectories.Dequeue();

            // The folder may well be gone again by now - a temp tree, an aborted install.
            if (!Directory.Exists(path)) continue;

            await WalkAsync(new DirectoryInfo(path), depth);
        }
    }

    /// <summary>Index the contents of a newly created directory, within this batch's budget.</summary>
    private Task IndexNewDirectoryAsync(DirectoryInfo root) => WalkAsync(root, 0);

    /// <summary>
    /// Walked with an explicit stack rather than by recursion. This used to call itself for every
    /// subdirectory with no guard of any kind, which had two consequences. A directory junction
    /// pointing at one of its own ancestors - mklink /J makes one in seconds, and C:\Users ships
    /// several - never terminated; being async recursion, each level cost a frame plus a state
    /// machine, so it ended in StackOverflowException, which cannot be caught and takes the
    /// process with it. Short of a loop, following any junction re-indexed a tree that is already
    /// indexed under its real path, inflating the database and the in-memory overlay with
    /// duplicates the unique (FolderId, Name) index cannot catch, because the paths differ.
    /// </summary>
    private async Task WalkAsync(DirectoryInfo root, int startDepth)
    {
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((root, startDepth));

        while (pending.Count > 0)
        {
            // A batch can be draining while the app is closing; a large new folder should not
            // hold that up (see FileWatcherService.StopAsync).
            if (_stopping) return;

            // Out of budget: hand the rest of the tree to the next batch and let this one finish.
            if (_walkBudget <= 0)
            {
                Defer(pending);
                return;
            }

            var (directory, depth) = pending.Pop();

            if (!await IndexFilesInAsync(directory))
            {
                // Ran out part way through this directory, so it is not finished with. Put it
                // back before deferring - INSERT OR IGNORE makes re-walking what it already wrote
                // free, whereas dropping it would leave the rest of the folder unindexed.
                pending.Push((directory, depth));
                Defer(pending);
                return;
            }

            if (depth >= MaxWatchIndexDepth)
            {
                Logger.Log($"Auto-watch stopped at depth {MaxWatchIndexDepth}: {directory.FullName}");
                continue;
            }

            await PushSubdirectoriesAsync(directory, depth, pending);
        }
    }

    /// <summary>Hand the unfinished part of a walk to the next batch.</summary>
    private void Defer(Stack<(DirectoryInfo Directory, int Depth)> pending)
    {
        foreach (var (directory, depth) in pending)
        {
            if (_deferredDirectories.Count >= MaxDeferredDirectories)
            {
                Logger.Log(
                    $"Auto-watch backlog full - {directory.FullName} left to the next catch-up pass.");
                return;
            }

            _deferredDirectories.Enqueue((directory.FullName, depth));
        }
    }

    /// <summary>
    /// Index the files sitting directly inside one directory.
    ///
    /// No per-file existence check: this only runs for directories that were not in the index a
    /// moment ago, and INSERT OR IGNORE against the unique (FolderId, Name) index already rejects
    /// a repeat. Asking first doubled the number of round trips through the shared connection -
    /// the one the search box also queues on - to establish what the insert settles anyway.
    /// </summary>
    /// <returns>False if it stopped early, so the caller knows the directory is unfinished.</returns>
    private async Task<bool> IndexFilesInAsync(DirectoryInfo directory)
    {
        try
        {
            foreach (var file in directory.EnumerateFiles())
            {
                if (_stopping || _walkBudget <= 0) return false;

                try
                {
                    if (IsExcludedExtension(file.Extension)) continue;
                    if (ShouldIgnore(file.FullName)) continue;

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
                    _walkBudget--;
                }
                catch { }
            }
        }
        catch
        {
            // Enumeration itself can fail part way through - access denied, or the directory
            // disappearing mid-walk. Give up on this directory only; the rest of the walk stands.
        }

        return true;
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
                if (_stopping) return;

                try
                {
                    if (IndexPlanner.IsSkippable(subDirectory)) continue;
                    if (ShouldIgnore(subDirectory.FullName)) continue;

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
                    _walkBudget--;

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
