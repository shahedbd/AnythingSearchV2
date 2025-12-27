namespace AnythingSearch.Models;
public class IndexProgress
{
    public long TotalFiles { get; set; }
    public long TotalFolders { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
    public int PercentComplete { get; set; }
    public double ItemsPerSecond { get; set; }
}