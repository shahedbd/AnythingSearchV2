using AnythingSearch.Database;
using AnythingSearch.Models;
using AnythingSearch.Services.Search.Memory;
using Xunit;

namespace AnythingSearch.Tests;

/// <summary>
/// Exercises the in-memory search index end to end: entries are written to a temporary SQLite
/// file, the snapshot is built from it, and queries are run against the snapshot.
///
/// The important property is that the in-memory index returns the SAME results, in the SAME
/// order, as the SQLite query it replaces - it is only meant to be faster, not different.
/// </summary>
public class MemorySearchTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AnythingSearchMem_{Guid.NewGuid():N}.db");
    private FileDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = new FileDatabase(_dbPath);
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
        return Task.CompletedTask;
    }

    private async Task AddAsync(string fullPath, bool isFolder = false, long size = 1024)
    {
        await _database.InsertSingleAsync(new FileEntry
        {
            Name = Path.GetFileName(fullPath),
            Path = fullPath,
            Extension = isFolder ? "" : Path.GetExtension(fullPath).TrimStart('.'),
            Size = size,
            Modified = new DateTime(2024, 5, 4, 3, 2, 1),
            IsFolder = isFolder
        });
    }

    /// <summary>Bulk variant - one transaction instead of one per row, for the large fixtures.</summary>
    private async Task AddManyAsync(IEnumerable<string> fullPaths)
    {
        await _database.BeginIncrementalTransactionAsync();
        try
        {
            foreach (var path in fullPaths) await AddAsync(path);
        }
        finally { await _database.CommitIncrementalTransactionAsync(); }
    }

    private MemoryFileIndex Build() =>
        MemoryIndexBuilder.Build(_dbPath, null, CancellationToken.None);

    private static List<string> Paths(MemoryFileIndex index, IEnumerable<int> hits) =>
        hits.Select(index.PathOf).ToList();

    [Fact]
    public async Task Search_FindsEntriesByNameRegardlessOfCase()
    {
        await AddAsync(@"G:\src\MsStoreAppDashboard\doc\TECH_SPEC.md");
        await AddAsync(@"C:\other\unrelated.txt");

        var index = Build();

        foreach (var query in new[] { "tech_spec", "TECH_SPEC", "Tech_Spec", "SPEC" })
        {
            var hits = index.Search(query, 100, CancellationToken.None, out var total);
            Assert.Equal(1, total);
            Assert.Equal(@"G:\src\MsStoreAppDashboard\doc\TECH_SPEC.md", index.PathOf(hits[0]));
        }
    }

    [Fact]
    public async Task Search_PreservesTheOriginalCasingOfNameAndPath()
    {
        await AddAsync(@"G:\src\MsStoreAppDashboard\doc\TECH_SPEC.md");

        var index = Build();
        var hits = index.Search("tech", 10, CancellationToken.None, out _);
        var entry = index.Materialize(hits[0]);

        Assert.Equal("TECH_SPEC.md", entry.Name);
        Assert.Equal(@"G:\src\MsStoreAppDashboard\doc\TECH_SPEC.md", entry.Path);
        Assert.Equal("md", entry.Extension);
        Assert.Equal(1024, entry.Size);
        Assert.Equal(new DateTime(2024, 5, 4, 3, 2, 1), entry.Modified);
        Assert.False(entry.IsFolder);
    }

    [Fact]
    public async Task Search_MatchesTheFolderPathToo()
    {
        await AddAsync(@"C:\projects\invoices\summary.txt");
        await AddAsync(@"C:\projects\other\readme.txt");

        var index = Build();
        var hits = index.Search("invoices", 100, CancellationToken.None, out var total);

        Assert.Equal(1, total);
        Assert.Equal(@"C:\projects\invoices\summary.txt", index.PathOf(hits[0]));
    }

    [Fact]
    public async Task Search_CountsAnEntryOnceWhenBothNameAndFolderMatch()
    {
        // "report" is in the folder path AND in the file name - it must not be counted twice.
        await AddAsync(@"C:\report\report.txt");
        await AddAsync(@"C:\report\other.txt");

        var index = Build();
        var hits = index.Search("report", 100, CancellationToken.None, out var total);

        Assert.Equal(2, total);
        Assert.Equal(2, hits.Count);
        Assert.Equal(hits.Count, hits.Distinct().Count());
    }

    [Fact]
    public async Task Search_OrdersFoldersFirstThenByRelevanceThenByShortestName()
    {
        await AddAsync(@"C:\data\catalog.txt");   // contains
        await AddAsync(@"C:\data\log.txt");       // starts with
        await AddAsync(@"C:\data\log");           // exact name
        await AddAsync(@"C:\data\logs", isFolder: true);

        var index = Build();
        var hits = index.Search("log", 100, CancellationToken.None, out var total);

        Assert.Equal(4, total);
        Assert.Equal(new[]
        {
            @"C:\data\logs",        // folders first
            @"C:\data\log",         // exact name match
            @"C:\data\log.txt",     // starts with
            @"C:\data\catalog.txt"  // contains
        }, Paths(index, hits));
    }

    [Fact]
    public async Task Search_RequiresEveryTermOfAMultiTermQuery()
    {
        await AddAsync(@"C:\src\app\main.cs");
        await AddAsync(@"C:\src\app\other.cs");
        await AddAsync(@"C:\lib\main.cs");

        var index = Build();
        var hits = index.Search("src main", 100, CancellationToken.None, out var total);

        Assert.Equal(1, total);
        Assert.Equal(@"C:\src\app\main.cs", index.PathOf(hits[0]));
    }

    [Fact]
    public async Task Search_ReportsTheTotalMatchCountEvenWhenTheLimitTruncatesTheResults()
    {
        await AddManyAsync(Enumerable.Range(0, 50).Select(i => $@"C:\bulk\item{i}.txt"));

        var index = Build();
        var hits = index.Search("item", 10, CancellationToken.None, out var total);

        Assert.Equal(50, total);
        Assert.Equal(10, hits.Count);
    }

    [Fact]
    public async Task Search_ReturnsNothingForATermThatIsNotPresent()
    {
        await AddAsync(@"C:\data\alpha.txt");

        var index = Build();
        var hits = index.Search("zzzznotthere", 100, CancellationToken.None, out var total);

        Assert.Empty(hits);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task Search_MatchesTheSqliteQueryItReplaces()
    {
        var paths = new[]
        {
            @"C:\data\log.txt", @"C:\data\catalog.txt", @"C:\data\log",
            @"C:\logs\alpha.txt", @"C:\logs\beta.txt", @"C:\other\unrelated.md",
        };
        foreach (var path in paths) await AddAsync(path);
        await AddAsync(@"C:\data\logs", isFolder: true);

        var index = Build();

        foreach (var query in new[] { "log", "txt", "data", "alpha" })
        {
            var fromSqlite = (await _database.SearchAsync(query, 100)).Select(r => r.Path).ToList();
            var fromMemory = Paths(index, index.Search(query, 100, CancellationToken.None, out _));

            Assert.Equal(fromSqlite, fromMemory);
        }
    }

    [Fact]
    public async Task Search_HandlesAnIndexLargeEnoughToBePartitionedAcrossThreads()
    {
        // Above the threshold where Search splits the scan over every core, so the partition
        // boundaries and the shared "already matched" bitmap are actually exercised.
        await AddManyAsync(Enumerable.Range(0, 60_000).Select(i => $@"C:\bulk\f{i % 100}\needle{i}.txt"));

        var index = Build();
        var hits = index.Search("needle", 5, CancellationToken.None, out var total);

        Assert.Equal(60_000, total);
        Assert.Equal(5, hits.Count);
        Assert.Equal(hits.Count, hits.Distinct().Count());

        var single = index.Search("needle42.txt", 10, CancellationToken.None, out var singleTotal);
        Assert.Equal(1, singleTotal);
        Assert.Equal(@"C:\bulk\f42\needle42.txt", index.PathOf(single[0]));
    }

    [Fact]
    public async Task Search_IsCancellable()
    {
        await AddManyAsync(Enumerable.Range(0, 60_000).Select(i => $@"C:\bulk\f{i % 100}\item{i}.txt"));

        var index = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => index.Search("item", 100, cts.Token, out _));
    }
}
