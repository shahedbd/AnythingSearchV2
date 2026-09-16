using System.Text.Json;

namespace AnythingSearch.Services;

public class RecentSearchService
{
    private readonly string _recentSearchesFile;
    private readonly int _maxRecentSearches = 10;
    private readonly int _minQueryLength = 3; // ✅ Minimum 3 characters
    private List<RecentSearchItem> _recentSearches = new();

    public RecentSearchService()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnythingSearch");

        Directory.CreateDirectory(appDataPath);
        _recentSearchesFile = Path.Combine(appDataPath, "recent_searches.json");

        LoadRecentSearches();
    }

    public void AddSearch(string query, int resultCount)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        // ✅ Only save searches with 3+ characters
        if (query.Length < _minQueryLength)
            return;

        // Remove existing entry if present
        _recentSearches.RemoveAll(x => x.Query.Equals(query, StringComparison.OrdinalIgnoreCase));

        // Add to top
        _recentSearches.Insert(0, new RecentSearchItem
        {
            Query = query,
            ResultCount = resultCount,
            Timestamp = DateTime.Now
        });

        // Keep only max items
        if (_recentSearches.Count > _maxRecentSearches)
        {
            _recentSearches = _recentSearches.Take(_maxRecentSearches).ToList();
        }

        SaveRecentSearches();
    }

    public List<RecentSearchItem> GetRecentSearches()
    {
        return _recentSearches.ToList();
    }

    public void ClearRecentSearches()
    {
        _recentSearches.Clear();
        SaveRecentSearches();
    }

    public void RemoveSearch(string query)
    {
        _recentSearches.RemoveAll(x => x.Query.Equals(query, StringComparison.OrdinalIgnoreCase));
        SaveRecentSearches();
    }

    private void LoadRecentSearches()
    {
        try
        {
            if (File.Exists(_recentSearchesFile))
            {
                var json = File.ReadAllText(_recentSearchesFile);
                _recentSearches = JsonSerializer.Deserialize<List<RecentSearchItem>>(json) ?? new();
            }
        }
        catch
        {
            _recentSearches = new();
        }
    }

    private void SaveRecentSearches()
    {
        try
        {
            var json = JsonSerializer.Serialize(_recentSearches, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_recentSearchesFile, json);
        }
        catch
        {
            // Ignore save errors
        }
    }
}

public class RecentSearchItem
{
    public string Query { get; set; } = string.Empty;
    public int ResultCount { get; set; }
    public DateTime Timestamp { get; set; }
}
