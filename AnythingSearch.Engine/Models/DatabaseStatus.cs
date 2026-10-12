using AnythingSearch.Helper;
using DeviceDataModule;
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
        ApplicationDataManager.Instance.ApplicationDataDirectory,
        "database_status.json");

    /// <summary>
    /// Path this instance persists to. Defaults to the shared app-data location; automated
    /// tests use <see cref="Load(string?)"/>'s override so they never touch real user data.
    /// (Private field - not serialized by System.Text.Json.)
    /// </summary>
    private string _filePath = StatusFilePath;

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
    /// Load status from JSON file.
    /// </summary>
    /// <param name="filePathOverride">
    /// Optional explicit file path, used by automated tests so they never touch the real
    /// user status file under %LocalAppData%. Production code should keep using the
    /// parameterless default.
    /// </param>
    public static DatabaseStatus Load(string? filePathOverride = null)
    {
        var path = filePathOverride ?? StatusFilePath;

        try
        {
            if (File.Exists(path))
            {
                var status = JsonSerializer.Deserialize<DatabaseStatus>(File.ReadAllText(path));
                if (status != null)
                {
                    status._filePath = path;

                    // If app crashed during indexing, mark as failed
                    if (status.State == DatabaseState.Indexing)
                    {
                        Logger.Log("Previous indexing run was interrupted - the indexing state " +
                                   "file still holds its checkpoints, so it will resume.");
                        status.State = DatabaseState.Failed;
                        status.ErrorMessage = "Indexing was interrupted (application closed unexpectedly)";
                        status.Save();
                    }
                    return status;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to load the database status file: {ex.Message}");
        }

        return new DatabaseStatus { _filePath = path };
    }

    /// <summary>
    /// Save status to JSON file
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to save the database status file: {ex.Message}");
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

    /// <summary>Wall-clock gap between progress saves, to keep this off the hot path.</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(5);

    private DateTime _lastProgressSave = DateTime.MinValue;

    /// <summary>
    /// Update progress during indexing.
    ///
    /// Saved on a timer rather than every N items: progress is now reported in large steps, so
    /// an item-count modulo test would fire almost never. Resumability does not depend on this
    /// file anyway - see <see cref="IndexingState"/> - so this is purely for display.
    /// </summary>
    public void UpdateProgress(long files, long folders, double speed, string currentPath)
    {
        TotalFiles = files;
        TotalFolders = folders;
        ItemsPerSecond = speed;
        CurrentPath = currentPath;
        LastUpdatedAt = DateTime.Now;

        if (DateTime.Now - _lastProgressSave < SaveInterval) return;

        _lastProgressSave = DateTime.Now;
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

        Logger.Log($"Index ready - {totalFiles:N0} files, {totalFolders:N0} folders.");
        Save();
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