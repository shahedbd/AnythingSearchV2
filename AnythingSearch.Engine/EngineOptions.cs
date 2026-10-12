using AnythingSearch.Models;

namespace AnythingSearch.Engine;

/// <summary>
/// Everything the engine needs from the app that hosts it. Each app calls <see cref="Configure"/>
/// once, first thing in Main, before any engine type is touched: DatabaseStatus and
/// IndexingState read <see cref="DataDirectory"/> when they are first loaded.
///
/// The engine never reads an app's settings service directly. <see cref="Settings"/> is a
/// provider instead, so live changes (the settings dialog) are seen on the next read, and an app
/// that hands out the same instance until it changes keeps the watcher's filter cache valid.
/// </summary>
public sealed class EngineOptions
{
    private static readonly IndexSettings DefaultSettings = new();

    public static EngineOptions Current { get; private set; } = new();

    public static void Configure(EngineOptions options) =>
        Current = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Folder holding the database, database_status.json and indexing_state.json.</summary>
    public string DataDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnythingSearch.Engine");

    public string DbFileName { get; init; } = "AnythingSearch.db";

    public string DbPath => Path.Combine(DataDirectory, DbFileName);

    /// <summary>
    /// Build idx_files_size ON Files(Size) WHERE IsFolder = 0 after the bulk build. Duplicate
    /// Finder ZX turns it on for its size grouping; Anything Search leaves it off.
    /// </summary>
    public bool CreateSizeIndex { get; init; }

    /// <summary>Exclusions, system-drive choice and indexing pace. Read on every use.</summary>
    public Func<IndexSettings> Settings { get; init; } = () => DefaultSettings;

    /// <summary>Where engine log lines go. Null drops them.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Runs on a pool thread whenever a complete index exists (after a build, or at startup when
    /// one is already there). Anything Search removes the previous release's data folder here.
    /// </summary>
    public Action? IndexComplete { get; init; }
}
