using AnythingSearch.Models;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace AnythingSearch.Database;

/// <summary>
/// High-performance file database optimized for both speed AND size.
/// 
/// Size optimizations:
/// - Normalized folder paths (stored once, referenced by ID)
/// - Compact integer IDs instead of repeated strings
/// - Checkpoint WAL after indexing to merge files
/// - VACUUM to reclaim space
/// 
/// Speed optimizations:
/// - Prepared statements with parameter reuse
/// - Large batch inserts
/// - Aggressive SQLite PRAGMA settings
/// - Indexes created AFTER bulk insert
/// </summary>
public partial class FileDatabase : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private static bool _initialized = false;

    // Thread-safe pending inserts
    private readonly ConcurrentBag<FileEntry> _pendingInserts = new();
    private readonly object _flushLock = new();
    private const int BulkInsertThreshold = 10000;

    // Folder path cache (path -> id) for normalized storage
    private readonly ConcurrentDictionary<string, long> _folderCache = new(StringComparer.OrdinalIgnoreCase);
    private long _nextFolderId = 1;
    private readonly object _folderLock = new();

    // Prepared statements
    private SqliteCommand? _insertFileCommand;
    private SqliteCommand? _insertFolderCommand;
    private bool _inTransaction = false;

    // One shared SqliteConnection is used by the search box, the file watcher and the index
    // catch-up pass, all on different threads. SQLite will not let the same connection write to
    // a table while one of its own readers is still open ("database table is locked"), so every
    // runtime operation is serialized through this gate. The bulk index build is not gated:
    // it owns the database exclusively while it runs.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateReleaser(_gate);
    }

    private sealed class GateReleaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        public GateReleaser(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose() => _semaphore.Release();
    }

    /// <param name="dbPathOverride">
    /// Optional explicit database file path, used by automated tests so they never touch the
    /// real user database under %LocalAppData%. Production code should keep using the
    /// parameterless default.
    /// </param>
    public FileDatabase(string? dbPathOverride = null)
    {
        if (!_initialized)
        {
            SQLitePCL.Batteries.Init();
            _initialized = true;
        }

        if (dbPathOverride != null)
        {
            _dbPath = dbPathOverride;
            return;
        }

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnythingSearch");

        Directory.CreateDirectory(appData);
        _dbPath = Path.Combine(appData, "AnythingSearch.db");
    }

    /// <summary>
    /// Location of the database file. The in-memory search index opens its own read-only
    /// connection to this path so building a snapshot never blocks the writer.
    /// </summary>
    public string DatabasePath => _dbPath;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath};Pooling=True;Cache=Shared");
        await _connection.OpenAsync();

        // Normalized schema: Folders table + Files table
        // This saves ~60-70% space compared to storing full paths
        var createTables = @"
            CREATE TABLE IF NOT EXISTS Folders (
                Id INTEGER PRIMARY KEY,
                Path TEXT NOT NULL COLLATE NOCASE
            );

            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE,
                FolderId INTEGER NOT NULL,
                Ext TEXT COLLATE NOCASE,
                Size INTEGER,
                Modified INTEGER,
                IsFolder INTEGER DEFAULT 0
            );
        ";

        using var cmd = new SqliteCommand(createTables, _connection);
        await cmd.ExecuteNonQueryAsync();

        await ExecuteNonQueryAsync("PRAGMA journal_mode = WAL");
        await ExecuteNonQueryAsync("PRAGMA temp_store = MEMORY");
        await ExecuteNonQueryAsync("PRAGMA page_size = 4096");
        await ExecuteNonQueryAsync("PRAGMA auto_vacuum = NONE");
        await ApplyRuntimeSettingsAsync();
    }

    /// <summary>
    /// Settings for ordinary running, as opposed to a bulk index build.
    ///
    /// These used to be the bulk-build settings, left in place for the entire session: a 256 MB
    /// page cache and a 512 MB mapping, held for the life of the process. That made sense when
    /// every keystroke queried SQLite, but searches are now answered from the in-memory index, so
    /// the page cache was several hundred megabytes doing nothing. synchronous is NORMAL here
    /// rather than OFF because incremental writes from the file watcher have to survive a crash;
    /// only the bulk build, which can simply be redone, turns it off.
    /// </summary>
    private async Task ApplyRuntimeSettingsAsync()
    {
        await ExecuteNonQueryAsync("PRAGMA synchronous = NORMAL");
        await ExecuteNonQueryAsync("PRAGMA cache_size = -16000");   // 16 MB
        await ExecuteNonQueryAsync("PRAGMA mmap_size = 67108864");  // 64 MB
        await ExecuteNonQueryAsync("PRAGMA locking_mode = NORMAL");
        // Wait briefly instead of failing outright when the in-memory index is reading the file.
        await ExecuteNonQueryAsync("PRAGMA busy_timeout = 5000");
    }

    /// <summary>Throughput settings for the bulk build, which owns the database while it runs.</summary>
    private async Task ApplyBulkBuildSettingsAsync()
    {
        await ExecuteNonQueryAsync("PRAGMA synchronous = OFF");
        await ExecuteNonQueryAsync("PRAGMA cache_size = -256000");
        await ExecuteNonQueryAsync("PRAGMA mmap_size = 536870912");
        await ExecuteNonQueryAsync("PRAGMA locking_mode = EXCLUSIVE");
    }

    public async Task ClearAsync()
    {
        // Clear caches
        while (_pendingInserts.TryTake(out _)) { }
        _folderCache.Clear();
        _nextFolderId = 1;

        // Drop and recreate for fastest clear
        await ExecuteNonQueryAsync("DROP TABLE IF EXISTS Files");
        await ExecuteNonQueryAsync("DROP TABLE IF EXISTS Folders");

        var createTables = @"
            CREATE TABLE Folders (
                Id INTEGER PRIMARY KEY,
                Path TEXT NOT NULL COLLATE NOCASE
            );

            CREATE TABLE Files (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE,
                FolderId INTEGER NOT NULL,
                Ext TEXT COLLATE NOCASE,
                Size INTEGER,
                Modified INTEGER,
                IsFolder INTEGER DEFAULT 0
            );
        ";

        using var cmd = new SqliteCommand(createTables, _connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task BeginBatchAsync()
    {
        // Only the bulk build takes the file exclusively, and only it gets the big page cache.
        // Holding either for the whole session (which is what InitializeAsync used to do) locked
        // out every other connection - including the read-only one the in-memory index is built
        // from - and kept hundreds of megabytes of cache reserved for a database that is no
        // longer on the search path.
        await ApplyBulkBuildSettingsAsync();
        await ExecuteNonQueryAsync("BEGIN TRANSACTION");
        _inTransaction = true;

        // Prepare insert statements for reuse
        _insertFolderCommand = new SqliteCommand(
            "INSERT INTO Folders (Id, Path) VALUES (@id, @path)",
            _connection);
        _insertFolderCommand.Parameters.Add("@id", SqliteType.Integer);
        _insertFolderCommand.Parameters.Add("@path", SqliteType.Text);
        _insertFolderCommand.Prepare();

        // OR IGNORE, paired with the UNIQUE index on (FolderId, Name), is what keeps the same
        // entry from being stored twice when two scan roots happen to cover the same directory.
        _insertFileCommand = new SqliteCommand(
            "INSERT OR IGNORE INTO Files (Name, FolderId, Ext, Size, Modified, IsFolder) VALUES (@n, @f, @e, @s, @m, @i)",
            _connection);
        _insertFileCommand.Parameters.Add("@n", SqliteType.Text);
        _insertFileCommand.Parameters.Add("@f", SqliteType.Integer);
        _insertFileCommand.Parameters.Add("@e", SqliteType.Text);
        _insertFileCommand.Parameters.Add("@s", SqliteType.Integer);
        _insertFileCommand.Parameters.Add("@m", SqliteType.Integer);
        _insertFileCommand.Parameters.Add("@i", SqliteType.Integer);
        _insertFileCommand.Prepare();
    }

    public async Task CommitBatchAsync()
    {
        await FlushPendingInsertsAsync();

        if (_inTransaction)
        {
            await ExecuteNonQueryAsync("COMMIT");
            _inTransaction = false;
        }

        _insertFileCommand?.Dispose();
        _insertFolderCommand?.Dispose();
        _insertFileCommand = null;
        _insertFolderCommand = null;
    }

    /// <summary>
    /// Create indexes and optimize database size AFTER bulk insert
    /// </summary>
    public async Task FinalizeIndexingAsync()
    {
        // Create indexes (much faster after all data is inserted)
        await ExecuteNonQueryAsync("CREATE INDEX IF NOT EXISTS idx_files_name ON Files(Name)");
        // Also the uniqueness guarantee: one entry per name per folder, which is what the file
        // system itself guarantees. Covers FolderId lookups too, so no separate index is needed.
        await ExecuteNonQueryAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_files_folder_name_unique ON Files(FolderId, Name)");
        await ExecuteNonQueryAsync("CREATE INDEX IF NOT EXISTS idx_files_ext ON Files(Ext)");
        await ExecuteNonQueryAsync("CREATE INDEX IF NOT EXISTS idx_folders_path ON Folders(Path)");

        // Checkpoint WAL to merge into main database file
        await ExecuteNonQueryAsync("PRAGMA wal_checkpoint(TRUNCATE)");

        // Back to the modest settings the app runs with the rest of the time
        await ApplyRuntimeSettingsAsync();

        // Optimize
        await ExecuteNonQueryAsync("ANALYZE");

        // VACUUM to reclaim space and compact the database
        // This can reduce size by 20-30%
        await ExecuteNonQueryAsync("VACUUM");
    }

    /// <summary>
    /// Thread-safe insert - adds to pending batch
    /// </summary>
    public Task InsertAsync(FileEntry entry)
    {
        _pendingInserts.Add(entry);

        if (_pendingInserts.Count >= BulkInsertThreshold)
        {
            return FlushPendingInsertsAsync();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get or create folder ID (thread-safe)
    /// </summary>
    private long GetOrCreateFolderId(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
            folderPath = "";

        if (_folderCache.TryGetValue(folderPath, out var existingId))
            return existingId;

        lock (_folderLock)
        {
            // Double-check after acquiring lock
            if (_folderCache.TryGetValue(folderPath, out existingId))
                return existingId;

            var newId = _nextFolderId++;
            _folderCache[folderPath] = newId;

            // Insert folder into database
            if (_insertFolderCommand != null)
            {
                _insertFolderCommand.Parameters["@id"].Value = newId;
                _insertFolderCommand.Parameters["@path"].Value = folderPath;
                _insertFolderCommand.ExecuteNonQuery();
            }

            return newId;
        }
    }

    /// <summary>
    /// Flush all pending inserts
    /// </summary>
    private async Task FlushPendingInsertsAsync()
    {
        if (!Monitor.TryEnter(_flushLock))
            return;

        try
        {
            var items = new List<FileEntry>(BulkInsertThreshold + 1000);
            while (_pendingInserts.TryTake(out var entry))
            {
                items.Add(entry);
            }

            if (items.Count == 0) return;

            if (_insertFileCommand != null)
            {
                foreach (var entry in items)
                {
                    // Get folder path and ID
                    var folderPath = entry.IsFolder
                        ? Path.GetDirectoryName(entry.Path) ?? ""
                        : Path.GetDirectoryName(entry.Path) ?? "";

                    var folderId = GetOrCreateFolderId(folderPath);

                    _insertFileCommand.Parameters["@n"].Value = entry.Name;
                    _insertFileCommand.Parameters["@f"].Value = folderId;
                    _insertFileCommand.Parameters["@e"].Value = entry.Extension ?? "";
                    _insertFileCommand.Parameters["@s"].Value = entry.Size;
                    _insertFileCommand.Parameters["@m"].Value = entry.Modified.Ticks;
                    _insertFileCommand.Parameters["@i"].Value = entry.IsFolder ? 1 : 0;

                    _insertFileCommand.ExecuteNonQuery();
                }
            }
        }
        finally
        {
            Monitor.Exit(_flushLock);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Fast search with JOIN - improved relevance sorting like Everything
    /// </summary>
    public async Task<List<FileEntry>> SearchAsync(string query, int limit = 1000, CancellationToken cancellationToken = default)
    {
        using var dbLock = await LockAsync();
        var results = new List<FileEntry>();

        // Escape special SQL LIKE characters - the ESCAPE '\' clause below tells SQLite
        // to use \ as escape character
        var escapedQuery = EscapeLike(query);

        // Search with relevance scoring:
        // 1. Exact name match (highest priority)
        // 2. Name starts with query
        // 3. Name contains query
        // 4. Path contains query (for finding files in matching folders)
        // Order by: folders first, then by relevance, then alphabetically
        // Written as two scans joined by UNION ALL rather than one OR across a join. The OR form
        // made SQLite drive the query from the Folders table ("SCAN fo" plus an index probe into
        // Files for every folder), so it walked the whole dataset and then sorted every match in a
        // temp B-tree. Here Files is scanned once for name matches, folders are matched
        // separately, and Folders is only joined for the rows that survived.
        //
        // This is the fallback path: it runs while the in-memory index is still loading, and if
        // that index is unavailable. MemorySearchService answers searches once it is ready.
        var sql = @"
            SELECT m.Name, fo.Path || '\' || m.Name AS FullPath, m.Ext, m.Size, m.Modified, m.IsFolder
            FROM (
                SELECT Name, FolderId, Ext, Size, Modified, IsFolder,
                    CASE
                        WHEN Name = @exact THEN 1
                        WHEN Name LIKE @startsWith ESCAPE '\' THEN 2
                        ELSE 3
                    END AS Relevance
                FROM Files
                WHERE Name LIKE @contains ESCAPE '\'

                UNION ALL

                SELECT Name, FolderId, Ext, Size, Modified, IsFolder, 4 AS Relevance
                FROM Files
                WHERE FolderId IN (SELECT Id FROM Folders WHERE Path LIKE @contains ESCAPE '\')
                  AND Name NOT LIKE @contains ESCAPE '\'
            ) m
            INNER JOIN Folders fo ON m.FolderId = fo.Id
            ORDER BY
                m.IsFolder DESC,
                m.Relevance ASC,
                LENGTH(m.Name) ASC,
                m.Name ASC
            LIMIT @limit
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@exact", query);
        cmd.Parameters.AddWithValue("@startsWith", $"{escapedQuery}%");
        cmd.Parameters.AddWithValue("@contains", $"%{escapedQuery}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // A superseded search (user kept typing) must stop reading rows immediately
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(new FileEntry
            {
                Name = reader.GetString(0),
                Path = reader.GetString(1),
                Extension = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Size = reader.GetInt64(3),
                Modified = new DateTime(reader.GetInt64(4)),
                IsFolder = reader.GetInt32(5) == 1
            });
        }

        return results;
    }

    /// <summary>
    /// Advanced search with multiple terms (space-separated)
    /// Each term must match somewhere in name or path
    /// </summary>
    public async Task<List<FileEntry>> SearchAdvancedAsync(string query, int limit = 1000, CancellationToken cancellationToken = default)
    {
        using var dbLock = await LockAsync();
        var results = new List<FileEntry>();
        
        // Split query into terms
        var terms = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        if (terms.Length == 0)
            return results;

        // Build WHERE clause for all terms (AND logic)
        var whereConditions = new List<string>();
        var parameters = new Dictionary<string, string>();
        
        for (int i = 0; i < terms.Length; i++)
        {
            // Escape special SQL LIKE characters using backslash
            var term = EscapeLike(terms[i]);
            var paramName = $"@t{i}";
            parameters[paramName] = $"%{term}%";
            whereConditions.Add($"(f.Name LIKE {paramName} ESCAPE '\\' OR fo.Path LIKE {paramName} ESCAPE '\\')");
        }

        var sql = $@"
            SELECT f.Name, fo.Path || '\' || f.Name AS FullPath, f.Ext, f.Size, f.Modified, f.IsFolder
            FROM Files f
            INNER JOIN Folders fo ON f.FolderId = fo.Id
            WHERE {string.Join(" AND ", whereConditions)}
            ORDER BY 
                f.IsFolder DESC,
                LENGTH(f.Name) ASC,
                f.Name ASC
            LIMIT @limit
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        foreach (var param in parameters)
        {
            cmd.Parameters.AddWithValue(param.Key, param.Value);
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // A superseded search (user kept typing) must stop reading rows immediately
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(new FileEntry
            {
                Name = reader.GetString(0),
                Path = reader.GetString(1),
                Extension = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Size = reader.GetInt64(3),
                Modified = new DateTime(reader.GetInt64(4)),
                IsFolder = reader.GetInt32(5) == 1
            });
        }

        return results;
    }

    public async Task<long> GetCountAsync()
    {
        using var dbLock = await LockAsync();
        using var cmd = new SqliteCommand("SELECT COUNT(*) FROM Files", _connection);
        var result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToInt64(result) : 0;
    }

    private async Task ExecuteNonQueryAsync(string sql)
    {
        using var cmd = new SqliteCommand(sql, _connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _insertFileCommand?.Dispose();
        _insertFolderCommand?.Dispose();
        _connection?.Dispose();
        _gate.Dispose();
    }
}