using AnythingSearch.Database;
using AnythingSearch.Models;
using AnythingSearch.Services;
using Xunit;

namespace AnythingSearch.Tests;

/// <summary>
/// Reproduces the reported bug: G:\src\MS_Store_App\MsStoreAppDashboard\doc\TECH_SPEC.md was
/// created after the index was built and never became searchable.
///
/// The tests build the same directory shape on disk (underscores in "MS_Store_App" matter - they
/// are LIKE wildcards), seed an index that predates the doc folder, then run the catch-up pass.
/// Everything runs against a temporary database, status file and directory tree.
/// </summary>
public class IndexCatchUpTests : IAsyncLifetime
{
    private const string SpecFileName = "TECH_SPEC.md";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"AnythingSearchCatchUp_{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"AnythingSearchCatchUp_{Guid.NewGuid():N}.db");
    private readonly string _statusPath = Path.Combine(
        Path.GetTempPath(), $"AnythingSearchCatchUp_{Guid.NewGuid():N}.json");

    private FileDatabase _database = null!;
    private BackgroundIndexingService _indexing = null!;

    private string ProjectDir => Path.Combine(_root, "src", "MS_Store_App", "MsStoreAppDashboard");
    private string DocDir => Path.Combine(ProjectDir, "doc");
    private string SpecPath => Path.Combine(DocDir, SpecFileName);

    public async Task InitializeAsync()
    {
        // The real tree: ...\src\MS_Store_App\MsStoreAppDashboard\{README.md, doc\TECH_SPEC.md}
        Directory.CreateDirectory(DocDir);
        File.WriteAllText(Path.Combine(ProjectDir, "README.md"), "readme");
        File.WriteAllText(SpecPath, "# Tech spec");

        _database = new FileDatabase(_dbPath);
        await _database.InitializeAsync();

        var settings = new SettingsManager();
        // Deterministic, in-memory only (never saved). The default list excludes
        // "AppData\Local\Temp", which is exactly where the test tree lives.
        settings.Settings.ExcludedFolders.Clear();
        settings.Settings.ExcludedExtensions.Clear();

        _indexing = new BackgroundIndexingService(_database, settings, _statusPath);

        await SeedIndexWithoutDocFolderAsync();
    }

    public Task DisposeAsync()
    {
        _indexing.Dispose();
        _database.Dispose();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
        TryDelete(_statusPath);
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// The state the real index was in: every folder up to the project indexed weeks ago, with
    /// no row for the doc folder or the file inside it.
    /// </summary>
    private async Task SeedIndexWithoutDocFolderAsync()
    {
        var staleTimestamp = DateTime.Now.AddDays(-30);

        var folders = new[]
        {
            Path.Combine(_root, "src"),
            Path.Combine(_root, "src", "MS_Store_App"),
            ProjectDir
        };

        foreach (var folder in folders)
            await _database.InsertSingleAsync(MakeFolder(folder, staleTimestamp));

        await _database.InsertSingleAsync(MakeFile(Path.Combine(ProjectDir, "README.md")));
    }

    private static FileEntry MakeFolder(string path, DateTime modified) => new()
    {
        Name = Path.GetFileName(path),
        Path = path,
        Extension = "",
        Size = 0,
        Modified = modified,
        IsFolder = true
    };

    private static FileEntry MakeFile(string path) => new()
    {
        Name = Path.GetFileName(path),
        Path = path,
        Extension = Path.GetExtension(path).TrimStart('.'),
        Size = new FileInfo(path).Length,
        Modified = File.GetLastWriteTime(path),
        IsFolder = false
    };

    [Fact]
    public async Task CatchUp_IndexesFileCreatedAfterTheIndexWasBuilt()
    {
        // The bug: not searchable before the catch-up runs
        Assert.Empty(await _database.SearchAsync(SpecFileName));

        var (added, _) = await _indexing.RunCatchUpForRootAsync(_root);

        Assert.True(added >= 2, $"expected the doc folder and {SpecFileName} to be added, got {added}");

        var results = await _database.SearchAsync(SpecFileName);
        var match = Assert.Single(results);
        Assert.Equal(SpecFileName, match.Name);
        Assert.Equal(SpecPath, match.Path);
    }

    [Fact]
    public async Task CatchUp_IsIdempotent()
    {
        await _indexing.RunCatchUpForRootAsync(_root);

        // Second pass sees matching folder timestamps and must not touch anything
        var (added, removed) = await _indexing.RunCatchUpForRootAsync(_root);

        Assert.Equal(0, added);
        Assert.Equal(0, removed);
        Assert.Single(await _database.SearchAsync(SpecFileName));
    }

    [Fact]
    public async Task CatchUp_RemovesEntriesDeletedWhileTheAppWasClosed()
    {
        await _indexing.RunCatchUpForRootAsync(_root);
        Assert.Single(await _database.SearchAsync(SpecFileName));

        File.Delete(SpecPath);

        var (_, removed) = await _indexing.RunCatchUpForRootAsync(_root);

        Assert.True(removed >= 1);
        Assert.Empty(await _database.SearchAsync(SpecFileName));
    }

    [Fact]
    public async Task SearchAsync_TreatsUnderscoreAsALiteralCharacter()
    {
        await _indexing.RunCatchUpForRootAsync(_root);

        // Same name with the underscore replaced - a raw LIKE pattern would match both
        await _database.InsertSingleAsync(new FileEntry
        {
            Name = "TECHXSPEC.md",
            Path = Path.Combine(DocDir, "TECHXSPEC.md"),
            Extension = "md",
            Size = 1,
            Modified = DateTime.Now,
            IsFolder = false
        });

        var results = await _database.SearchAsync("TECH_SPEC");

        var match = Assert.Single(results);
        Assert.Equal(SpecFileName, match.Name);
    }

    [Fact]
    public async Task DeleteByPathAsync_DoesNotDeleteSiblingFoldersMatchingAnUnderscoreWildcard()
    {
        await _indexing.RunCatchUpForRootAsync(_root);

        // "MS.Store.App" matches the LIKE pattern "MS_Store_App\%" unless '_' is escaped
        var sibling = Path.Combine(_root, "src", "MS.Store.App", "Sub");
        await _database.InsertSingleAsync(MakeFolder(sibling, DateTime.Now));
        await _database.InsertSingleAsync(new FileEntry
        {
            Name = "other.txt",
            Path = Path.Combine(sibling, "other.txt"),
            Extension = "txt",
            Size = 1,
            Modified = DateTime.Now,
            IsFolder = false
        });

        await _database.DeleteByPathAsync(Path.Combine(_root, "src", "MS.Store.App"));

        // The real project's file must survive
        Assert.Single(await _database.SearchAsync(SpecFileName));
    }

    [Fact]
    public async Task SearchesAndWritesCanRunConcurrentlyOnTheSharedConnection()
    {
        await _indexing.RunCatchUpForRootAsync(_root);

        var searches = Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
                await _database.SearchAsync(SpecFileName);
        });

        var writes = Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                await _database.InsertSingleAsync(new FileEntry
                {
                    Name = $"concurrent_{i}.txt",
                    Path = Path.Combine(DocDir, $"concurrent_{i}.txt"),
                    Extension = "txt",
                    Size = 1,
                    Modified = DateTime.Now,
                    IsFolder = false
                });
            }
        });

        await Task.WhenAll(searches, writes);

        Assert.Single(await _database.SearchAsync(SpecFileName));
    }
}
