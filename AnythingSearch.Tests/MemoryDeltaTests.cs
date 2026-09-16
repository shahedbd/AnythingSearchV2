using AnythingSearch.Database;
using AnythingSearch.Models;
using AnythingSearch.Services.Search.Memory;
using Xunit;

namespace AnythingSearch.Tests;

/// <summary>
/// The in-memory snapshot is immutable, so changes the file watcher makes after it was built are
/// layered on top as a delta. These cover that overlay: a file created a moment ago must be
/// findable, and one just deleted must stop showing up, without waiting for a rebuild.
/// </summary>
public class MemoryDeltaTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AnythingSearchDelta_{Guid.NewGuid():N}.db");
    private FileDatabase _database = null!;
    private MemorySearchService _service = null!;

    public async Task InitializeAsync()
    {
        _database = new FileDatabase(_dbPath);
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _service?.Dispose();
        _database.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
        return Task.CompletedTask;
    }

    private static FileEntry Entry(string fullPath, bool isFolder = false) => new()
    {
        Name = Path.GetFileName(fullPath),
        Path = fullPath,
        Extension = isFolder ? "" : Path.GetExtension(fullPath).TrimStart('.'),
        Size = 512,
        Modified = new DateTime(2025, 2, 3),
        IsFolder = isFolder
    };

    /// <summary>Loads a snapshot of whatever is currently in the database.</summary>
    private async Task StartServiceAsync()
    {
        _service = new MemorySearchService(_dbPath);
        _service.Start();
        await _service.FirstSnapshotSettled;
        Assert.True(_service.IsReady);
    }

    private List<string> Search(string query, int limit = 100)
    {
        Assert.True(_service.TrySearch(query, limit, CancellationToken.None, out var results, out _));
        return results.Select(r => r.Path).ToList();
    }

    private int TotalFor(string query)
    {
        Assert.True(_service.TrySearch(query, 100, CancellationToken.None, out _, out var total));
        return total;
    }

    [Fact]
    public async Task TrySearch_ReportsNotReadyBeforeTheFirstSnapshotIsLoaded()
    {
        _service = new MemorySearchService(_dbPath);

        Assert.False(_service.TrySearch("anything", 10, CancellationToken.None, out var results, out var total));
        Assert.Empty(results);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task NotifyAdded_MakesAFileFoundBeforeTheNextRebuild()
    {
        await _database.InsertSingleAsync(Entry(@"C:\data\existing.txt"));
        await StartServiceAsync();

        Assert.Empty(Search("brandnew"));

        _service.NotifyAdded(Entry(@"C:\data\brandnew.txt"));

        Assert.Equal(new[] { @"C:\data\brandnew.txt" }, Search("brandnew"));
        Assert.Equal(1, TotalFor("brandnew"));
    }

    [Fact]
    public async Task NotifyRemoved_HidesAFileThatIsStillInTheSnapshot()
    {
        await _database.InsertSingleAsync(Entry(@"C:\data\doomed.txt"));
        await StartServiceAsync();

        Assert.Single(Search("doomed"));

        _service.NotifyRemoved(@"C:\data\doomed.txt");

        Assert.Empty(Search("doomed"));
        Assert.Equal(0, TotalFor("doomed"));
    }

    [Fact]
    public async Task NotifyRemoved_ThenNotifyAdded_LeavesTheFileVisibleExactlyOnce()
    {
        await _database.InsertSingleAsync(Entry(@"C:\data\churn.txt"));
        await StartServiceAsync();

        // An editor saving atomically: delete, then recreate.
        _service.NotifyRemoved(@"C:\data\churn.txt");
        _service.NotifyAdded(Entry(@"C:\data\churn.txt"));

        Assert.Equal(new[] { @"C:\data\churn.txt" }, Search("churn"));
    }

    [Fact]
    public async Task NotifyUpdated_ReplacesTheEntryRatherThanDuplicatingIt()
    {
        await _database.InsertSingleAsync(Entry(@"C:\data\growing.log"));
        await StartServiceAsync();

        var bigger = Entry(@"C:\data\growing.log");
        bigger.Size = 999_999;
        _service.NotifyUpdated(bigger);

        Assert.True(_service.TrySearch("growing", 10, CancellationToken.None, out var results, out var total));
        Assert.Single(results);
        Assert.Equal(1, total);
        Assert.Equal(999_999, results[0].Size);
    }

    [Fact]
    public async Task NotifyAdded_EntriesAreRankedAlongsideSnapshotEntries()
    {
        await _database.InsertSingleAsync(Entry(@"C:\data\catalog.txt"));   // contains
        await StartServiceAsync();

        _service.NotifyAdded(Entry(@"C:\data\log.txt"));                    // starts with
        _service.NotifyAdded(Entry(@"C:\data\logs", isFolder: true));       // folder

        Assert.Equal(new[]
        {
            @"C:\data\logs",
            @"C:\data\log.txt",
            @"C:\data\catalog.txt"
        }, Search("log"));
    }

    [Fact]
    public async Task Count_TracksAdditionsAndRemovals()
    {
        await _database.InsertSingleAsync(Entry(@"C:\data\a.txt"));
        await _database.InsertSingleAsync(Entry(@"C:\data\b.txt"));
        await StartServiceAsync();

        Assert.Equal(2, _service.Count);

        _service.NotifyAdded(Entry(@"C:\data\c.txt"));
        Assert.Equal(3, _service.Count);

        _service.NotifyRemoved(@"C:\data\a.txt");
        Assert.Equal(2, _service.Count);
    }

    [Fact]
    public async Task ManyPendingChanges_AreAppliedWithoutQuadraticCost()
    {
        await _database.InsertSingleAsync(Entry(@"C:\data\seed.txt"));
        await StartServiceAsync();

        // The delta used to be rebuilt from scratch on every single change, which turned a large
        // batch of watcher events into O(n^2) work. 15,000 changes should be near-instant.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 15_000; i++)
            _service.NotifyAdded(Entry($@"C:\data\batch\file{i}.txt"));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed.TotalSeconds < 5,
            $"applying 15,000 changes took {stopwatch.Elapsed.TotalSeconds:F1}s");

        Assert.Equal(15_000, TotalFor("file"));
        Assert.Equal(15_001, _service.Count);
    }

    [Fact]
    public async Task RequestRebuild_FoldsPendingChangesIntoTheSnapshot()
    {
        await _database.InsertSingleAsync(Entry(@"C:\data\first.txt"));
        await StartServiceAsync();

        // Written to the database and mirrored into the delta, the way the watcher does it.
        await _database.InsertSingleAsync(Entry(@"C:\data\second.txt"));
        _service.NotifyAdded(Entry(@"C:\data\second.txt"));
        Assert.Single(Search("second"));

        var rebuilt = new TaskCompletionSource();
        _service.SnapshotReady += () => rebuilt.TrySetResult();
        _service.RequestRebuild("test");
        await rebuilt.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Still exactly one result - the snapshot now holds it and the delta no longer does.
        Assert.Equal(new[] { @"C:\data\second.txt" }, Search("second"));
        Assert.Equal(2, _service.Count);
    }
}
