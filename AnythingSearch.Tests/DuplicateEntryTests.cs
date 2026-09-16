using AnythingSearch.Database;
using AnythingSearch.Models;
using AnythingSearch.Services.Search.Memory;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AnythingSearch.Tests;

/// <summary>
/// A file system cannot hold two entries with the same name in the same folder, but the schema
/// never enforced that, so every insert path was free to store another copy of a row that was
/// already there. Indexes built by earlier versions accumulated large numbers of duplicates,
/// which both slowed every search down and showed the same file to the user several times.
/// </summary>
public class DuplicateEntryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AnythingSearchDup_{Guid.NewGuid():N}.db");
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

    private static FileEntry Entry(string fullPath) => new()
    {
        Name = Path.GetFileName(fullPath),
        Path = fullPath,
        Extension = Path.GetExtension(fullPath).TrimStart('.'),
        Size = 10,
        Modified = new DateTime(2024, 1, 1),
        IsFolder = false
    };

    /// <summary>Writes a row straight past the app's insert path, the way an older build did.</summary>
    private void InsertRaw(string fullPath)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        var folder = Path.GetDirectoryName(fullPath) ?? "";
        using (var cmd = new SqliteCommand("INSERT INTO Folders (Path) SELECT @p WHERE NOT EXISTS (SELECT 1 FROM Folders WHERE Path = @p COLLATE NOCASE)", connection))
        {
            cmd.Parameters.AddWithValue("@p", folder);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = new SqliteCommand(
            "INSERT INTO Files (Name, FolderId, Ext, Size, Modified, IsFolder) " +
            "SELECT @n, Id, '', 10, 0, 0 FROM Folders WHERE Path = @p COLLATE NOCASE", connection))
        {
            cmd.Parameters.AddWithValue("@n", Path.GetFileName(fullPath));
            cmd.Parameters.AddWithValue("@p", folder);
            cmd.ExecuteNonQuery();
        }
    }

    private long RowCount()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        connection.Open();
        using var cmd = new SqliteCommand("SELECT COUNT(*) FROM Files", connection);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task EnsureUniqueEntriesAsync_RemovesDuplicatesLeftByEarlierVersions()
    {
        InsertRaw(@"C:\data\report.txt");
        InsertRaw(@"C:\data\report.txt");
        InsertRaw(@"C:\data\report.txt");
        InsertRaw(@"C:\data\other.txt");
        Assert.Equal(4, RowCount());

        var removed = await _database.EnsureUniqueEntriesAsync();

        Assert.Equal(2, removed);
        Assert.Equal(2, RowCount());

        var results = await _database.SearchAsync("report");
        Assert.Single(results);
    }

    [Fact]
    public async Task EnsureUniqueEntriesAsync_IsANoOpOnACleanIndexAndSafeToRepeat()
    {
        await _database.InsertSingleAsync(Entry(@"C:\data\report.txt"));

        Assert.Equal(0, await _database.EnsureUniqueEntriesAsync());
        Assert.Equal(0, await _database.EnsureUniqueEntriesAsync());
        Assert.Equal(1, RowCount());
    }

    [Fact]
    public async Task InsertSingleAsync_CannotAddTheSameEntryTwiceOnceUniquenessIsEnforced()
    {
        await _database.EnsureUniqueEntriesAsync();

        await _database.InsertSingleAsync(Entry(@"C:\data\report.txt"));
        await _database.InsertSingleAsync(Entry(@"C:\data\report.txt"));
        await _database.InsertSingleAsync(Entry(@"C:\data\report.txt"));

        Assert.Equal(1, RowCount());
        Assert.Single(await _database.SearchAsync("report"));
    }

    [Fact]
    public async Task UpdatePathAsync_ReplacesAnEntryAlreadyStoredUnderTheDestinationName()
    {
        await _database.EnsureUniqueEntriesAsync();
        await _database.InsertSingleAsync(Entry(@"C:\data\old.txt"));
        await _database.InsertSingleAsync(Entry(@"C:\data\new.txt"));

        // Renaming old.txt onto new.txt: the stored new.txt row is stale and must be replaced,
        // not left behind as a UNIQUE violation.
        var updated = await _database.UpdatePathAsync(@"C:\data\old.txt", @"C:\data\new.txt");

        Assert.Equal(1, updated);
        Assert.Equal(1, RowCount());
        Assert.Empty(await _database.SearchAsync("old.txt"));
        Assert.Single(await _database.SearchAsync("new.txt"));
    }

    [Fact]
    public async Task CompactIfFragmentedAsync_ReclaimsSpaceLeftByTheDuplicateCleanUp()
    {
        // Enough rows that the freed pages are a meaningful share of the file.
        await _database.BeginIncrementalTransactionAsync();
        try
        {
            for (int i = 0; i < 4000; i++)
                await _database.InsertSingleAsync(Entry($@"C:\data\folder{i % 20}\file{i}.txt"));
        }
        finally { await _database.CommitIncrementalTransactionAsync(); }

        // Delete most of them, which only marks pages free inside the file.
        for (int i = 0; i < 3500; i++)
            await _database.DeleteByPathAsync($@"C:\data\folder{i % 20}\file{i}.txt");

        // minimumBytesToReclaim is lowered because this fixture is kilobytes, not the hundreds of
        // megabytes a real index weighs.
        var reclaimed = await _database.CompactIfFragmentedAsync(minimumBytesToReclaim: 4096);

        Assert.True(reclaimed > 0, "expected the fragmented database to be compacted");
        Assert.Equal(500, RowCount());

        // Nothing left to reclaim, so a second call is a no-op.
        Assert.Equal(0, await _database.CompactIfFragmentedAsync(minimumBytesToReclaim: 4096));
    }

    [Fact]
    public async Task CompactIfFragmentedAsync_LeavesATightlyPackedDatabaseAlone()
    {
        await _database.InsertSingleAsync(Entry(@"C:\data\report.txt"));

        Assert.Equal(0, await _database.CompactIfFragmentedAsync(minimumBytesToReclaim: 4096));
    }

    [Fact]
    public async Task CompactIfFragmentedAsync_DoesNotRewriteTheDatabaseForATrivialSaving()
    {
        // Heavily fragmented in relative terms, but only kilobytes are at stake - rewriting a
        // multi-hundred-megabyte index to save that would cost far more than it returns.
        await _database.BeginIncrementalTransactionAsync();
        try
        {
            for (int i = 0; i < 2000; i++)
                await _database.InsertSingleAsync(Entry($@"C:\data\file{i}.txt"));
        }
        finally { await _database.CommitIncrementalTransactionAsync(); }

        for (int i = 0; i < 1900; i++)
            await _database.DeleteByPathAsync($@"C:\data\file{i}.txt");

        Assert.Equal(0, await _database.CompactIfFragmentedAsync());
        Assert.Equal(100, RowCount());
    }

    [Fact]
    public async Task DeduplicatedIndex_ShowsEachFileOnceInMemorySearchResults()
    {
        InsertRaw(@"C:\data\report.txt");
        InsertRaw(@"C:\data\report.txt");

        // Before the clean-up the snapshot inherits whatever the database holds...
        var before = MemoryIndexBuilder.Build(_dbPath, null, CancellationToken.None);
        before.Search("report", 10, CancellationToken.None, out var beforeTotal);
        Assert.Equal(2, beforeTotal);

        await _database.EnsureUniqueEntriesAsync();

        // ...and after it, each file appears exactly once.
        var after = MemoryIndexBuilder.Build(_dbPath, null, CancellationToken.None);
        var hits = after.Search("report", 10, CancellationToken.None, out var afterTotal);
        Assert.Equal(1, afterTotal);
        Assert.Equal(@"C:\data\report.txt", after.PathOf(hits[0]));
    }
}
