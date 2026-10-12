using System.Collections.Concurrent;
using AnythingSearch.Helper;

namespace AnythingSearch.Services;

/// <summary>
/// Decides how hard to drive the disk while indexing, per drive, from the hardware actually
/// present. Replaces a single compiled-in walker count that had to be low enough for the worst
/// machine the app ships to and was therefore wrong for nearly every other one.
///
/// Why per drive and not per machine: the scopes the planner builds are per drive
/// (see <see cref="IndexPlanner.BuildScopes"/>), and a mixed machine is ordinary - the one this
/// was written on has a solid-state system drive, three mechanical disks and a virtual drive,
/// and they do not all want the same answer.
///
/// What bounds the numbers below:
///
/// * The walk itself. This is the binding limit, and it is measured - see
///   <see cref="WalkersForMedia"/>. Concurrency stops paying at four walkers whatever the
///   device is, so solid-state drives are raised to four and nothing goes higher.
/// * The disk. Solid state tolerates concurrent walks; a mechanical disk is the case where
///   extra walkers could turn a sequential read into a seek. That is why the device is
///   identified at all, and why only solid state is raised.
/// * The CPU. Walkers allocate an entry per file and hand it to a channel, so they need cores.
///   One is always left for the database writer and the UI.
/// * The writer. Every walker feeds one SQLite consumer, so that consumer is a hard ceiling.
///   Measured on its own it absorbs about 276,000 rows a second, against the roughly 21,000
///   items a second the whole pipeline achieved with two walkers - so the writer has more than
///   an order of magnitude of headroom and is not what limits the walker count. It is still
///   worth stating: if the writer is ever made slower, this stops being true.
/// * The user. Indexing is background work. On battery the counts come back down and the full
///   throttle is kept, because a laptop that empties its battery indexing is a worse outcome
///   than one that finishes a few minutes later.
///
/// Every limit stops at the number the app already used, so no machine can come out of this
/// slower than it went in.
///
/// Users who have set a number themselves keep it: <see cref="Models.AppSettings.AutoTuneIndexing"/>
/// turns all of this off and <see cref="Models.AppSettings.MaxIndexingThreads"/> is used verbatim.
/// </summary>
public static class IndexingCapacity
{
    /// <summary>What the app used for every machine before any of this existed.</summary>
    private const int DefaultWalkers = 2;

    /// <summary>
    /// Concurrent walkers per drive, before the machine-wide limits are applied.
    ///
    /// Four is not a guess - it is where the measurement stops improving. Walking 208,000 entries
    /// across eight trees on this machine's SSD, with the metadata already cached so the disk is
    /// out of the picture and only the enumeration path is being measured:
    ///
    ///     1 walker   32,000 items/sec   x1.00
    ///     2 walkers  38,500 items/sec   x1.20   (what the app shipped with)
    ///     4 walkers  61,300 items/sec   x1.91
    ///     8 walkers  61,100 items/sec   x1.91   - nothing
    ///    12 walkers  62,200 items/sec   x1.94   - nothing
    ///
    /// The ceiling is in the walk itself rather than in the device, which is why NVMe is not
    /// given more than SATA: there is nothing above four for a faster device to collect. Nor is
    /// the database writer the limit - measured on its own it absorbs about 276,000 rows a
    /// second, against the roughly 21,000 items a second the whole pipeline managed with two
    /// walkers, so it has more than an order of magnitude spare.
    ///
    /// Nothing is given LESS than <see cref="DefaultWalkers"/>. A mechanical disk is the obvious
    /// candidate for dropping to one walker, and that is what this originally did - but every
    /// attempt to measure it here was swamped by how much the shape of a directory tree matters
    /// (the same drive gave anything from 12,000 to 127,000 items/sec depending only on which
    /// tree was walked), and the Windows metadata cache cannot be dropped to get a clean cold
    /// run. Lowering it would be an unmeasured regression for a large group of users, so
    /// rotational, removable and unrecognised devices all keep exactly the pacing they have now
    /// and this change can only ever make a machine faster.
    /// </summary>
    private static int WalkersForMedia(StorageMedia media) => media switch
    {
        StorageMedia.Nvme => 4,
        StorageMedia.SolidState => 4,
        _ => DefaultWalkers
    };

    /// <summary>
    /// Entries a walker emits between pauses, and how long it pauses. The pause exists so the
    /// machine stays usable during a build; on a device fast enough not to be disturbed by the
    /// walk it is pure lost time, so it is relaxed as the device gets faster and kept in full
    /// wherever the disk is the thing the user would notice.
    /// </summary>
    private static (int BatchSize, int DelayMs) ThrottleForMedia(StorageMedia media) => media switch
    {
        StorageMedia.Nvme => (8000, 0),
        StorageMedia.SolidState => (4000, 2),
        _ => (2000, 4)                  // rotational, removable and unknown keep the old pacing
    };

    /// <summary>Below this much RAM, stay close to the old behaviour whatever the disk is.</summary>
    private const long LowMemoryBytes = 4L * 1024 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, byte> Explained = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many scan units to walk at once on the drive holding <paramref name="path"/>.
    /// Never returns less than 1.
    /// </summary>
    public static int WalkersFor(string path)
    {
        var settings = SettingsService.Current;
        if (!settings.AutoTuneIndexing)
            return Math.Max(1, settings.MaxIndexingThreads);

        var profile = StorageProfiler.Profile(path);
        var walkers = WalkersForMedia(profile.Media);
        var reasons = profile.Description;

        // The floor every limit below stops at. Dropping under what the app already used would
        // be slowing a machine down on a guess, which is the one thing this must not do - the
        // exception being a single-core machine, where there is no core to spare for a second
        // walker in the first place.
        var floor = Environment.ProcessorCount <= 1 ? 1 : DefaultWalkers;

        // One core stays free for the SQLite writer and the message pump. Without this a
        // four-core machine would hand every core to walkers and feel unresponsive for the
        // whole build.
        var cpuLimit = Math.Max(floor, Environment.ProcessorCount - 1);
        if (walkers > cpuLimit)
        {
            walkers = cpuLimit;
            reasons += $", {Environment.ProcessorCount} core(s)";
        }

        // Walkers cost memory in flight - the bounded channel plus an entry each - and on a
        // small machine that competes with the snapshot the app is about to build.
        if (walkers > floor && TotalMemoryBytes() < LowMemoryBytes)
        {
            walkers = floor;
            reasons += ", low memory";
        }

        // Re-read on every scope rather than cached with the hardware: a laptop can be unplugged
        // part way through a build, and the next drive should respect that.
        if (OnBattery() && walkers > floor)
        {
            walkers = Math.Max(floor, walkers / 2);
            reasons += ", on battery";
        }

        Explain(path, $"{walkers} walker(s) - {reasons}");
        return walkers;
    }

    /// <summary>
    /// The throttle for the drive holding <paramref name="path"/>: how many entries a walker
    /// emits between pauses, and how long it pauses for. A delay of 0 disables the pause.
    /// </summary>
    public static (int BatchSize, int DelayMs) ThrottleFor(string path)
    {
        var settings = SettingsService.Current;
        if (!settings.AutoTuneIndexing)
            return (Math.Max(0, settings.IndexThrottleBatchSize), Math.Max(0, settings.IndexThrottleDelayMs));

        // On battery the disk is not the scarce resource - the battery is. Keep the full pause
        // regardless of how fast the device is.
        if (OnBattery()) return ThrottleForMedia(StorageMedia.Unknown);

        return ThrottleForMedia(StorageProfiler.Profile(path).Media);
    }

    private static bool OnBattery()
    {
        try
        {
            return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        }
        catch { return false; }
    }

    private static long TotalMemoryBytes()
    {
        try
        {
            // What the runtime is allowed to use, which is the figure that matters - on a machine
            // with a job-object or container limit it is lower than the installed RAM.
            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return available > 0 ? available : long.MaxValue;
        }
        catch { return long.MaxValue; }
    }

    /// <summary>
    /// Log the decision once per drive. With this many different machines in the field, a
    /// support report has to be able to say what the app chose and why without a repro.
    /// </summary>
    private static void Explain(string path, string decision)
    {
        var root = TryGetRoot(path);
        if (!Explained.TryAdd(root, 0)) return;

        Logger.Log($"Indexing {root}: {decision}");
    }

    private static string TryGetRoot(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? path; }
        catch { return path; }
    }
}
