namespace AnythingSearch.Models;

public class FileEntry
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public bool IsFolder { get; set; }
}
