namespace AnythingSearch.Models;

/// <summary>
/// Indicates which search source SearchManager is currently using.
/// </summary>
public enum SearchSource
{
    /// <summary>
    /// No search available
    /// </summary>
    None,

    /// <summary>
    /// Using Windows Search Index
    /// </summary>
    WindowsSearch,

    /// <summary>
    /// Using local SQLite database
    /// </summary>
    SQLite
}
