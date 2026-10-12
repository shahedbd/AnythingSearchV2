using AnythingSearch.Models;

namespace AnythingSearch.Services;

/// <summary>
/// Receives every change the file watcher applies to the database, so a host-side copy of the
/// index (Anything Search's in-memory search) can stay in step without re-reading it.
/// </summary>
public interface IIndexChangeSink
{
    void NotifyAdded(FileEntry entry);
    void NotifyRemoved(string path);
    void NotifyUpdated(FileEntry entry);
}
