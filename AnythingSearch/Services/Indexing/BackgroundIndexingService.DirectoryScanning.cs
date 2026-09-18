using System.Threading.Channels;
using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// The disk-walking producer side of <see cref="BackgroundIndexingService"/>: walks one unit and
/// writes its entries into the channel drained by BackgroundIndexingService.Consumer.cs.
///
/// Which directories become units, and in what order, is decided by <see cref="IndexPlanner"/>.
/// This file is only concerned with walking one of them politely: a bounded channel provides
/// back-pressure when the database writer falls behind, and a short pause after every throttle
/// batch keeps the disk from sitting at 100% for the whole run.
/// </summary>
public partial class BackgroundIndexingService
{
    /// <summary>
    /// Walk one unit. A non-recursive unit contributes only the files directly inside it - its
    /// subdirectories are units of their own.
    /// </summary>
    private void ScanUnit(ScanRoot root, HashSet<string> skipDirectories, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        // A unit's own root is checked here as well as when subdirectories are queued: the
        // planner can hand out a junction as a unit root (C:\Users is full of them), and
        // walking it would index a tree that is already indexed under its real path.
        if (IndexPlanner.IsSkippable(root.Directory)) return;

        var writer = _channel!.Writer;

        // Paced for the drive being walked. The pause is there to keep the machine usable, so a
        // device the user would not notice being walked does not need as much of it.
        var (throttleBatch, throttleDelay) = IndexingCapacity.ThrottleFor(root.Directory.FullName);

        var directoryStack = new Stack<DirectoryInfo>(64);
        directoryStack.Push(root.Directory);

        int sinceThrottle = 0;

        while (directoryStack.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var currentDirectory = directoryStack.Pop();
            var fullName = currentDirectory.FullName;

            if (_planner.IsExcluded(fullName)) continue;
            if (IsSkipped(fullName, skipDirectories)) continue;

            _currentPath = fullName;

            // A drive root has no parent folder to be listed in, so it gets no entry of its own.
            if (currentDirectory.Parent != null)
            {
                Write(writer, new FileEntry
                {
                    Name = currentDirectory.Name,
                    Path = fullName,
                    Extension = "",
                    Size = 0,
                    Modified = SafeLastWrite(currentDirectory),
                    IsFolder = true
                }, cancellationToken);

                Interlocked.Increment(ref _scopeFolders);
                Interlocked.Increment(ref _totalFolders);
                Throttle(ref sinceThrottle, throttleBatch, throttleDelay);
            }

            ScanFiles(currentDirectory, writer, ref sinceThrottle, throttleBatch, throttleDelay, cancellationToken);

            if (!root.Recursive) continue;

            PushSubdirectories(currentDirectory, directoryStack, skipDirectories, cancellationToken);
        }
    }

    /// <summary>
    /// Emit every eligible file directly inside a directory. The counters are bumped per entry
    /// rather than per unit so the progress line keeps moving even inside a huge directory tree.
    /// </summary>
    private void ScanFiles(
        DirectoryInfo directory,
        ChannelWriter<FileEntry> writer,
        ref int sinceThrottle,
        int throttleBatch,
        int throttleDelay,
        CancellationToken cancellationToken)
    {
        IEnumerator<FileInfo> enumerator;
        try { enumerator = directory.EnumerateFiles().GetEnumerator(); }
        catch { return; }

        using (enumerator)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                FileInfo file;
                try
                {
                    if (!enumerator.MoveNext()) break;
                    file = enumerator.Current;
                }
                catch { break; }

                try
                {
                    var extension = file.Extension;
                    if (_planner.IsExcludedExtension(extension)) continue;

                    Write(writer, new FileEntry
                    {
                        Name = file.Name,
                        Path = file.FullName,
                        Extension = extension.Length > 0 ? extension.Substring(1) : "",
                        Size = file.Length,
                        Modified = file.LastWriteTime,
                        IsFolder = false
                    }, cancellationToken);

                    Interlocked.Increment(ref _scopeFiles);
                    Interlocked.Increment(ref _totalFiles);
                    Throttle(ref sinceThrottle, throttleBatch, throttleDelay);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }
    }

    private void PushSubdirectories(
        DirectoryInfo directory,
        Stack<DirectoryInfo> directoryStack,
        HashSet<string> skipDirectories,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var subDirectory in directory.EnumerateDirectories())
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    if (IndexPlanner.IsSkippable(subDirectory)) continue;
                    if (_planner.IsExcluded(subDirectory.FullName)) continue;
                    if (IsSkipped(subDirectory.FullName, skipDirectories)) continue;

                    directoryStack.Push(subDirectory);
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool IsSkipped(string path, HashSet<string> skipDirectories)
        => skipDirectories.Count > 0 &&
           skipDirectories.Contains(path.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>
    /// Hand an entry to the writer, waiting when the channel is full instead of spinning. The
    /// wait is the back-pressure that keeps the walkers from reading faster than the database
    /// can absorb - which is what made memory use explode before the channel was bounded low.
    /// </summary>
    private static void Write(ChannelWriter<FileEntry> writer, FileEntry entry, CancellationToken cancellationToken)
    {
        while (!writer.TryWrite(entry))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!writer.WaitToWriteAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
                throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// Pause briefly after every batch of entries. Without this a walker reads as fast as the
    /// disk allows, which on a mechanical drive means 100% utilisation for the whole run and a
    /// machine that feels frozen. A delay of 0 in settings disables the throttle entirely.
    /// </summary>
    private static void Throttle(ref int sinceThrottle, int throttleBatch, int throttleDelay)
    {
        if (throttleBatch <= 0 || throttleDelay <= 0) return;

        if (++sinceThrottle < throttleBatch) return;

        sinceThrottle = 0;
        Thread.Sleep(throttleDelay);
    }

    private static DateTime SafeLastWrite(DirectoryInfo directory)
    {
        try { return directory.LastWriteTime; }
        catch { return DateTime.MinValue; }
    }
}
