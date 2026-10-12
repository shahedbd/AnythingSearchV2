using AnythingSearch.Helper;
using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Per-change-type database sync logic for FileWatcherService. The ignore filters these call
/// live in FileWatcherService.Filters.cs; the walk a genuinely new directory triggers lives in
/// FileWatcherService.DirectoryWalk.cs.
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

            // The created path can itself be the junction - `mklink /J` raises a Created event
            // like any other new directory. Skipped whole, row included, which is what the bulk
            // indexer does (ScanUnit returns before writing anything for a skippable root), so a
            // rebuild and the watcher end up with the same contents.
            if (IndexPlanner.IsSkippable(dir)) return;

            // Already indexed - and that is the overwhelmingly common case here, because a
            // directory raises a Changed event of its own every time a file is added to or
            // removed from it, and HandleModifiedAsync routes those back into this method.
            //
            // Returning early is the single most important thing this class does for search
            // latency. Without it, saving one file in a large folder walked that folder's ENTIRE
            // subtree again - a database round trip per file, each one taking the connection gate
            // the search box also has to queue behind. A busy disk kept that walk running
            // permanently, which is how a five-character query ended up waiting 87 seconds.
            //
            // Nothing is lost by skipping it: the entries inside an already-indexed directory
            // raise their own events, and anything that happened while the app was closed belongs
            // to the startup catch-up pass, not to a change notification.
            if (await _database.ExistsAsync(path)) return;

            var folderEntry = new FileEntry
            {
                Name = dir.Name,
                Path = dir.FullName,
                Extension = "",
                Size = 0,
                Modified = dir.LastWriteTime,
                IsFolder = true
            };
            await _database.InsertSingleAsync(folderEntry);
            _memoryIndex?.NotifyAdded(folderEntry);

            // Genuinely new to the index, so its contents may never raise events of their own -
            // a folder moved in from another drive, or restored from a backup, arrives whole.
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
}
