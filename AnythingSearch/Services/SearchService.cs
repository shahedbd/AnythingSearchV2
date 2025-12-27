using System.Diagnostics;
using AnythingSearch.Models;
using AnythingSearch.Database;

namespace AnythingSearch.Services;

public class SearchService
{
    private readonly FileDatabase _database;

    public SearchService(FileDatabase database)
    {
        _database = database;
    }

    public async Task<(List<FileEntry> Results, long TimeMs)> SearchAsync(string query, int maxResults = 1000)
    {
        var sw = Stopwatch.StartNew();
        var results = await _database.SearchAsync(query, maxResults);
        sw.Stop();

        return (results, sw.ElapsedMilliseconds);
    }

    public async Task<long> GetTotalCountAsync()
    {
        return await _database.GetCountAsync();
    }
}
