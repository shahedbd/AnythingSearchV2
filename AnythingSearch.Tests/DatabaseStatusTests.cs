using AnythingSearch.Models;
using Xunit;

namespace AnythingSearch.Tests;

/// <summary>
/// Exercises DatabaseStatus's persisted state machine against a temporary file (via the
/// filePathOverride test seam), never the real %LocalAppData%\AnythingSearch\database_status.json
/// used by the running app.
/// </summary>
public class DatabaseStatusTests : IDisposable
{
    private readonly string _statusPath =
        Path.Combine(Path.GetTempPath(), $"AnythingSearchTests_status_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_statusPath)) File.Delete(_statusPath); } catch { }
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsNotStarted()
    {
        var status = DatabaseStatus.Load(_statusPath);

        Assert.Equal(DatabaseState.NotStarted, status.State);
        Assert.False(status.IsReady);
        Assert.False(status.IsIndexing);
        Assert.Equal(0, status.TotalItems);
    }

    [Fact]
    public void MarkIndexingStarted_SetsIndexingState_AndPersists()
    {
        var status = DatabaseStatus.Load(_statusPath);

        status.MarkIndexingStarted();

        Assert.True(status.IsIndexing);
        Assert.NotNull(status.IndexingStartedAt);

        var reloaded = DatabaseStatus.Load(_statusPath);
        Assert.Equal(DatabaseState.Indexing, reloaded.State);
    }

    [Fact]
    public void MarkCompleted_SetsReadyState_AndTotals()
    {
        var status = DatabaseStatus.Load(_statusPath);
        status.MarkIndexingStarted();

        status.MarkCompleted(totalFiles: 1000, totalFolders: 50);

        Assert.True(status.IsReady);
        Assert.Equal(1050, status.TotalItems);
        Assert.Null(status.ErrorMessage);
    }

    [Fact]
    public void MarkFailed_SetsFailedState_WithErrorMessage()
    {
        var status = DatabaseStatus.Load(_statusPath);

        status.MarkFailed("disk read error");

        Assert.Equal(DatabaseState.Failed, status.State);
        Assert.False(status.IsReady);
        Assert.Contains("disk read error", status.GetStatusMessage());
    }

    [Fact]
    public void Reset_ClearsCountersAndState()
    {
        var status = DatabaseStatus.Load(_statusPath);
        status.MarkCompleted(500, 10);

        status.Reset();

        Assert.Equal(DatabaseState.NotStarted, status.State);
        Assert.Equal(0, status.TotalItems);
        Assert.Null(status.IndexingCompletedAt);
    }

    [Fact]
    public void Load_WhenPreviousRunWasInterruptedMidIndexing_MarksAsFailed()
    {
        // Simulate an app that crashed while State was still "Indexing"
        var crashed = DatabaseStatus.Load(_statusPath);
        crashed.MarkIndexingStarted();

        var reloaded = DatabaseStatus.Load(_statusPath);

        Assert.Equal(DatabaseState.Failed, reloaded.State);
        Assert.Contains("interrupted", reloaded.ErrorMessage);
    }
}
