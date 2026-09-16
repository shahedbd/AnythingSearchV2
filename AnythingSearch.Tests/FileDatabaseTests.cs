using AnythingSearch.Database;
using AnythingSearch.Models;
using Xunit;

namespace AnythingSearch.Tests;

/// <summary>
/// Exercises FileDatabase against a temporary SQLite file (via the dbPathOverride test seam),
/// never the real %LocalAppData%\AnythingSearch\AnythingSearch.db used by the running app.
/// </summary>
public class FileDatabaseTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AnythingSearchTests_{Guid.NewGuid():N}.db");
    private FileDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = new FileDatabase(_dbPath);
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static FileEntry MakeFile(string name, string path) => new()
    {
        Name = name,
        Path = path,
        Extension = Path.GetExtension(name).TrimStart('.'),
        Size = 1024,
        Modified = DateTime.Now,
        IsFolder = false
    };

    [Fact]
    public async Task InsertSingleAsync_ThenSearchAsync_FindsTheFile()
    {
        await _database.InsertSingleAsync(MakeFile("report.docx", @"C:\Users\Test\Documents\report.docx"));

        var results = await _database.SearchAsync("report");

        Assert.Contains(results, r => r.Name == "report.docx");
    }

    [Fact]
    public async Task SearchAsync_RanksNameStartsWithAboveNameContains()
    {
        await _database.InsertSingleAsync(MakeFile("log.txt", @"C:\Data\log.txt"));
        await _database.InsertSingleAsync(MakeFile("catalog.txt", @"C:\Data\catalog.txt"));

        var results = await _database.SearchAsync("log");

        // "log.txt" starts with "log" (higher relevance); "catalog.txt" only contains it.
        Assert.Equal("log.txt", results.First().Name);
    }

    [Fact]
    public async Task DeleteByPathAsync_RemovesTheEntry()
    {
        var path = @"C:\Users\Test\Documents\temp.txt";
        await _database.InsertSingleAsync(MakeFile("temp.txt", path));

        Assert.True(await _database.ExistsAsync(path));

        await _database.DeleteByPathAsync(path);

        Assert.False(await _database.ExistsAsync(path));
    }

    [Fact]
    public async Task SearchAdvancedAsync_RequiresAllTermsToMatch()
    {
        await _database.InsertSingleAsync(MakeFile("annual report.docx", @"C:\Users\Test\Documents\annual report.docx"));
        await _database.InsertSingleAsync(MakeFile("report.docx", @"C:\Users\Test\Documents\report.docx"));

        var results = await _database.SearchAdvancedAsync("annual report");

        Assert.Single(results);
        Assert.Equal("annual report.docx", results[0].Name);
    }

    [Fact]
    public async Task GetCountAsync_ReflectsInsertedRowCount()
    {
        await _database.InsertSingleAsync(MakeFile("a.txt", @"C:\Data\a.txt"));
        await _database.InsertSingleAsync(MakeFile("b.txt", @"C:\Data\b.txt"));

        var count = await _database.GetCountAsync();

        Assert.Equal(2, count);
    }
}
