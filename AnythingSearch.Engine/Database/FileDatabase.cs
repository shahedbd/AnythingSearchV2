using AnythingSearch.Models;
using DeviceDataModule;
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
/// - Search indexes created AFTER bulk insert (the uniqueness index has to exist during it -
///   see PrepareForBulkIndexingAsync)
/// </summary>
public partial class FileDatabase : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private static bool _initialized = false;

    // Thread-safe pending inserts
    private readonly ConcurrentBag<FileEntry> _pendingInserts = new();

    // 0 = idle, 1 = a flush is running. An int flag rather than a Monitor lock: the flush now
    // awaits the connection gate, and a Monitor lock cannot be released on a different thread
    // than the one that took it - which is exactly what happens after an await.
    private int _flushing;
    private const int BulkInsertThreshold = 10000;

    // Folder path cache (path -> id) for normalized storage
    private readonly ConcurrentDictionary<string, long> _folderCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _folderLock = new();

    // Prepared statements
    private SqliteCommand? _insertFileCommand;
    private SqliteCommand? _insertFolderCommand;
    private SqliteCommand? _selectFolderCommand;
    private bool _inTransaction = false;

    // One shared SqliteConnection is used by the search box, the file watcher, the index
    // catch-up pass and the index build, all on different threads. SQLite will not let the same
    // connection write to a table while one of its own readers is still open ("database table is
    // locked"), so every operation is serialized through this gate - including the bulk build,
    // which no longer owns the database exclusively now that phases publish while later ones
    // are still running.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Set by <see cref="Dispose"/> before the connection is torn down, so a caller arriving
    /// late is refused with a clear exception instead of whatever the disposed
    /// SqliteConnection happens to throw from somewhere deep inside a query.
    ///
    /// The orderly shutdown path (SearchManager.ShutdownAsync, FileWatcherService.StopAsync)
    /// exists so nothing should ever be in flight this late. This is the backstop for the paths
    /// that cannot be awaited - a watcher batch that overran its timeout, say - and every
    /// caller of this type already treats a failed database operation as recoverable.
    /// </summary>
    private volatile bool _disposed;

    private async Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Re-checked after the wait: the gate may have been held by the operation that was still
        // running when shutdown began, and the database can have been disposed while we queued.
        if (_disposed)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(FileDatabase));
        }

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

        // ApplicationDataManager, not a hand-built %LocalAppData% path: it creates and
        // validates the directory and falls back when it is unwritable, which a hard-coded
        // Path.Combine cannot do.
        _dbPath = Path.Combine(
            ApplicationDataManager.Instance.ApplicationDataDirectory, "AnythingSearch.db");
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

        // Lowering cache_size caps how large the page cache may GROW; it does not hand back pages
        // SQLite has already allocated. That matters on the path into here from
        // ApplyBulkBuildSettingsAsync, which runs the whole index build with a 64 MB cache: without
        // this the process keeps holding that cache for the rest of the session, long after the
        // build that justified it finished and searches moved to the in-memory index.
        await ExecuteNonQueryAsync("PRAGMA shrink_memory");
    }

    /// <summary>
    /// Throughput settings for a bulk build.
    ///
    /// locking_mode stays NORMAL, unlike the old rebuild-everything build: indexing now publishes
    /// each phase as it finishes, so the in-memory index opens its read-only connection to this
    /// same file while later phases are still writing, and EXCLUSIVE would lock it out. The page
    /// cache is 64 MB rather than 256 MB for the same reason - the process is answering searches
    /// at the same time, and that memory is better spent on the search index.
    /// </summary>
    private async Task ApplyBulkBuildSettingsAsync()
    {
        await ExecuteNonQueryAsync("PRAGMA synchronous = OFF");
        await ExecuteNonQueryAsync("PRAGMA cache_size = -64000");
        await ExecuteNonQueryAsync("PRAGMA mmap_size = 268435456");
        await ExecuteNonQueryAsync("PRAGMA locking_mode = NORMAL");
    }

    /// <summary>
    /// Get the database ready for a phased index build.
    ///
    /// The unique index on (FolderId, Name) is created BEFORE the inserts, not after. Phased
    /// indexing writes into a database that already holds earlier phases, and a resumed run
    /// re-walks the chunk that was in flight when it stopped, so INSERT OR IGNORE needs the index
    /// in place to drop the repeats. That costs some insert throughput and buys resumability
    /// without duplicates. The name and extension indexes are still built at the end, where they
    /// are far cheaper.
    /// </summary>
    public async Task PrepareForBulkIndexingAsync()
    {
        using var dbLock = await LockAsync();
        await ApplyBulkBuildSettingsAsync();
        await ExecuteNonQueryAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_files_folder_name_unique ON Files(FolderId, Name)");
        await ExecuteNonQueryAsync("CREATE INDEX IF NOT EXISTS idx_folders_path ON Folders(Path)");
    }

    public async Task ClearAsync()
    {
        using var dbLock = await LockAsync();

        // Clear caches
        while (_pendingInserts.TryTake(out _)) { }
        _folderCache.Clear();

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

    /// <summary>
    /// Open a transaction for one chunk of the index build. Called once per chunk rather than
    /// once per run: a chunk-sized transaction is what makes a checkpoint durable, so an
    /// interrupted build keeps everything it had already committed.
    /// </summary>
    public async Task BeginBatchAsync()
    {
        using var dbLock = await LockAsync();

        if (_inTransaction) return;

        await ExecuteNonQueryAsync("BEGIN TRANSACTION");
        _inTransaction = true;

        // Folder ids are assigned by SQLite, not by a private counter: the Folders table now
        // survives across phases and across restarts, so a counter starting at 1 would collide
        // with rows written by an earlier phase.
        _insertFolderCommand = new SqliteCommand(
            "INSERT INTO Folders (Path) VALUES (@path); SELECT last_insert_rowid();",
            _connection);
        _insertFolderCommand.Parameters.Add("@path", SqliteType.Text);
        _insertFolderCommand.Prepare();

        _selectFolderCommand = new SqliteCommand(
            "SELECT Id FROM Folders WHERE Path = @path COLLATE NOCASE",
            _connection);
        _selectFolderCommand.Parameters.Add("@path", SqliteType.Text);
        _selectFolderCommand.Prepare();

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
        // Flush first, on its own: it takes the same gate, which is not reentrant.
        await FlushPendingInsertsAsync();

        using var dbLock = await LockAsync();

        if (_inTransaction)
        {
            await ExecuteNonQueryAsync("COMMIT");
            _inTransaction = false;
        }

        _insertFileCommand?.Dispose();
        _insertFolderCommand?.Dispose();
        _selectFolderCommand?.Dispose();
        _insertFileCommand = null;
        _insertFolderCommand = null;
        _selectFolderCommand = null;
    }

    /// <summary>
    /// Wrap up one scope (phase 1, or a single drive) before its data is published: fold the
    /// write-ahead log back into the database file so the snapshot build that follows reads one
    /// file instead of two. Deliberately cheap - ANALYZE and VACUUM wait for
    /// <see cref="FinalizeIndexingAsync"/>, which runs once at the very end.
    /// </summary>
    public async Task FinalizeScopeAsync()
    {
        using var dbLock = await LockAsync();
        await ExecuteNonQueryAsync("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    /// <summary>
    /// Create indexes and optimize database size AFTER bulk insert
    /// </summary>
    public async Task FinalizeIndexingAsync()
    {
        using var dbLock = await LockAsync();

        // The unique index and idx_folders_path already exist - PrepareForBulkIndexingAsync needs
        // them during the build, and both are used: (FolderId, Name) answers every Name lookup
        // the watcher and the catch-up pass make, and idx_folders_path answers the path lookup
        // behind each of them.
        //
        // Two more indexes used to be built here, on Files(Name) and Files(Ext). Both are gone,
        // because EXPLAIN QUERY PLAN says neither can ever be chosen and between them they were
        // 51.6 MiB of a 191 MiB index - 27% of the file:
        //
        //   Files(Name) cannot serve the search. The query is Name LIKE '%term%', and a leading
        //   wildcard rules a B-tree out; the ESCAPE '\' clause disables SQLite's LIKE-to-range
        //   optimisation on top of that. The plan for it is "SCAN Files". Every other query that
        //   matches on Name also constrains FolderId, which (FolderId, Name) already covers.
        //
        //   Files(Ext) was never read at all - its own CREATE was the only mention of Ext in any
        //   statement this class issues.
        //
        // See DropUnusedIndexesAsync for databases that already have them.
        await ExecuteNonQueryAsync("PRAGMA wal_checkpoint(TRUNCATE)");

        // Back to the modest settings the app runs with the rest of the time
        await ApplyRuntimeSettingsAsync();

        // Optimize
        await ExecuteNonQueryAsync("ANALYZE");

        // VACUUM to reclaim space and compact the database
        // This can reduce size by 20-30%
        await ExecuteNonQueryAsync("VACUUM");

        // VACUUM in WAL mode rebuilds the database INTO the write-ahead log, so without this the
        // main file never shrinks and the log is left holding a second copy of everything. After
        // a 1.3 million entry build that was a 191 MiB database beside a 192 MiB log - the
        // footprint was double what it should be, and the first in-memory snapshot then had to
        // read both. CompactIfFragmentedAsync has always checkpointed after its VACUUM for
        // exactly this reason; this path simply did not.
        await ExecuteNonQueryAsync("PRAGMA wal_checkpoint(TRUNCATE)");
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

            // The folder may already be stored by an earlier phase or an earlier run, so the
            // database is consulted before a new row is added. Only a genuine miss costs a query.
            if (_selectFolderCommand != null)
            {
                _selectFolderCommand.Parameters["@path"].Value = folderPath;
                var found = _selectFolderCommand.ExecuteScalar();
                if (found != null && found != DBNull.Value)
                {
                    var existing = Convert.ToInt64(found);
                    _folderCache[folderPath] = existing;
                    return existing;
                }
            }

            if (_insertFolderCommand == null) return 0;

            _insertFolderCommand.Parameters["@path"].Value = folderPath;
            var newId = Convert.ToInt64(_insertFolderCommand.ExecuteScalar());
            _folderCache[folderPath] = newId;
            return newId;
        }
    }

    /// <summary>
    /// Flush all pending inserts
    /// </summary>
    private async Task FlushPendingInsertsAsync()
    {
        if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0)
            return;

        try
        {
            // Bulk writes used to bypass the connection gate, because a full rebuild owned the
            // database. Phased indexing publishes as it goes, so a search or a count can now
            // reach the shared connection while a chunk is being written - and
            // Microsoft.Data.Sqlite connections are not thread-safe. One gate acquisition per
            // batch, not per row.
            using var dbLock = await LockAsync();

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
            Volatile.Write(ref _flushing, 0);
        }
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
        if (_disposed) return;

        // Set first, so anything that reaches LockAsync from here on is turned away rather than
        // being handed a connection that is about to go away underneath it.
        _disposed = true;

        _insertFileCommand?.Dispose();
        _insertFolderCommand?.Dispose();
        _selectFolderCommand?.Dispose();
        _connection?.Dispose();

        // _gate is deliberately NOT disposed. SemaphoreSlim only holds an OS resource once
        // AvailableWaitHandle has been read, which nothing here does, so disposing it buys
        // nothing - and it would throw ObjectDisposedException into any thread still parked in
        // WaitAsync, which is exactly the shutdown crash this guard exists to prevent.
    }
}