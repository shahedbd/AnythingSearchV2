using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnythingSearch.Models;

/// <summary>
/// Tracks the status of the SQLite database indexing progress.
/// Persisted to JSON file to survive application restarts.
/// </summary>
public class DatabaseStatus
{
    private static readonly string StatusFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AnythingSearch",
        "database_status.json");

    /// <summary>
    /// Current state of the database
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DatabaseState State { get; set; } = DatabaseState.NotStarted;

    /// <summary>
    /// Total number of files indexed
    /// </summary>
    public long TotalFiles { get; set; }

    /// <summary>
    /// Total number of folders indexed
    /// </summary>
    public long TotalFolders { get; set; }

    /// <summary>
    /// When indexing started
    /// </summary>
    public DateTime? IndexingStartedAt { get; set; }

    /// <summary>
    /// When indexing completed
    /// </summary>
    public DateTime? IndexingCompletedAt { get; set; }

    /// <summary>
    /// Last time the database was updated
    /// </summary>
    public DateTime? LastUpdatedAt { get; set; }

    /// <summary>
    /// Current indexing speed (items per second)
    /// </summary>
    public double ItemsPerSecond { get; set; }

    /// <summary>
    /// Current path being indexed
    /// </summary>
    public string CurrentPath { get; set; } = string.Empty;

    /// <summary>
    /// Error message if indexing failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Version of the database schema
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Whether the database is ready to be used for searching
    /// </summary>
    [JsonIgnore]
    public bool IsReady => State == DatabaseState.Ready;

    /// <summary>
    /// Whether indexing is currently in progress
    /// </summary>
    [JsonIgnore]
    public bool IsIndexing => State == DatabaseState.Indexing;

    /// <summary>
    /// Total items indexed (files + folders)
    /// </summary>
    [JsonIgnore]
    public long TotalItems => TotalFiles + TotalFolders;

    /// <summary>
    /// Load status from JSON file
    /// </summary>
    public static DatabaseStatus Load()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[DatabaseStatus] Loading from: {StatusFilePath}");

            if (File.Exists(StatusFilePath))
            {
                var json = File.ReadAllText(StatusFilePath);
                System.Diagnostics.Debug.WriteLine($"[DatabaseStatus] JSON content: {json}");

                var status = JsonSerializer.Deserialize<DatabaseStatus>(json);
                if (status != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[DatabaseStatus] Loaded state: {status.State}, TotalItems: {status.TotalItems}");

                    // If app crashed during indexing, mark as failed
                    if (status.State == DatabaseState.Indexing)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DatabaseStatus] Previous indexing was interrupted, marking as failed");
                        status.State = DatabaseState.Failed;
                        status.ErrorMessage = "Indexing was interrupted (application closed unexpectedly)";
                        status.Save();
                    }
                    return status;
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[DatabaseStatus] Status file does not exist, returning new status");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DatabaseStatus] Failed to load: {ex.Message}");
        }

        return new DatabaseStatus();
    }

    /// <summary>
    /// Save status to JSON file
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(StatusFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(StatusFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save database status: {ex.Message}");
        }
    }

    /// <summary>
    /// Reset status for a fresh indexing run
    /// </summary>
    public void Reset()
    {
        State = DatabaseState.NotStarted;
        TotalFiles = 0;
        TotalFolders = 0;
        IndexingStartedAt = null;
        IndexingCompletedAt = null;
        LastUpdatedAt = null;
        ItemsPerSecond = 0;
        CurrentPath = string.Empty;
        ErrorMessage = null;
        Save();
    }

    /// <summary>
    /// Mark indexing as started
    /// </summary>
    public void MarkIndexingStarted()
    {
        State = DatabaseState.Indexing;
        IndexingStartedAt = DateTime.Now;
        IndexingCompletedAt = null;
        ErrorMessage = null;
        Save();
    }

    /// <summary>
    /// Update progress during indexing
    /// </summary>
    public void UpdateProgress(long files, long folders, double speed, string currentPath)
    {
        TotalFiles = files;
        TotalFolders = folders;
        ItemsPerSecond = speed;
        CurrentPath = currentPath;
        LastUpdatedAt = DateTime.Now;

        // Save periodically (every 10,000 items to avoid excessive I/O)
        if ((TotalItems % 10000) == 0)
            Save();
    }

    /// <summary>
    /// Mark indexing as completed successfully
    /// </summary>
    public void MarkCompleted(long totalFiles, long totalFolders)
    {
        State = DatabaseState.Ready;
        TotalFiles = totalFiles;
        TotalFolders = totalFolders;
        IndexingCompletedAt = DateTime.Now;
        LastUpdatedAt = DateTime.Now;
        CurrentPath = string.Empty;
        ErrorMessage = null;

        System.Diagnostics.Debug.WriteLine($"[DatabaseStatus] Marking as READY - Files: {totalFiles}, Folders: {totalFolders}");
        Save();
        System.Diagnostics.Debug.WriteLine($"[DatabaseStatus] Status saved to: {StatusFilePath}");
    }


    /// <summary>
    /// Mark indexing as failed
    /// </summary>
    public void MarkFailed(string errorMessage)
    {
        State = DatabaseState.Failed;
        ErrorMessage = errorMessage;
        LastUpdatedAt = DateTime.Now;
        Save();
    }

    /// <summary>
    /// Get a human-readable status message
    /// </summary>
    public string GetStatusMessage()
    {
        return State switch
        {
            DatabaseState.NotStarted => "Database not initialized",
            DatabaseState.Indexing => $"Indexing: {TotalItems:N0} items ({ItemsPerSecond:N0}/sec)",
            DatabaseState.Ready => $"Ready: {TotalItems:N0} items indexed",
            DatabaseState.Failed => $"Failed: {ErrorMessage}",
            DatabaseState.Updating => $"Updating: {TotalItems:N0} items",
            _ => "Unknown state"
        };
    }
}

/// <summary>
/// Possible states of the database
/// </summary>
public enum DatabaseState
{
    /// <summary>
    /// Database has never been indexed
    /// </summary>
    NotStarted,

    /// <summary>
    /// Indexing is in progress
    /// </summary>
    Indexing,

    /// <summary>
    /// Database is ready for use
    /// </summary>
    Ready,

    /// <summary>
    /// Indexing failed
    /// </summary>
    Failed,

    /// <summary>
    /// Database is being updated (incremental)
    /// </summary>
    Updating
}