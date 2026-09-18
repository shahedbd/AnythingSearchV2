using Microsoft.Data.Sqlite;

namespace AnythingSearch.Database;

/// <summary>
/// Schema maintenance that runs once per database, not once per search.
///
/// A file system cannot hold two entries with the same name in the same folder, but the schema
/// never said so, so every insert path was free to add another copy of a row that was already
/// there. Databases built by earlier versions accumulated a large number of duplicates that way
/// (on the machine this was developed against, 1.4 million redundant rows - 42% of the index).
/// Duplicates are expensive twice over: every search scans them, and the user sees the same file
/// listed several times.
///
/// <see cref="EnsureUniqueEntriesAsync"/> makes that impossible going forward by putting a UNIQUE
/// index on (FolderId, Name), cleaning out any existing duplicates first. All insert paths use
/// INSERT OR IGNORE so a redundant write is simply dropped instead of throwing.
/// </summary>
public partial class FileDatabase
{
    private const string UniqueIndexName = "idx_files_folder_name_unique";

    /// <summary>Reports progress of the one-off duplicate clean-up.</summary>
    public event Action<string>? MaintenanceStatusChanged;

    /// <summary>
    /// Guarantee that (FolderId, Name) is unique, removing existing duplicates if needed.
    /// Returns the number of redundant rows deleted. Safe to call on every startup: once the
    /// unique index exists this is a single catalogue lookup.
    /// </summary>
    public async Task<long> EnsureUniqueEntriesAsync(CancellationToken cancellationToken = default)
    {
        using var dbLock = await LockAsync(cancellationToken);

        if (await IndexExistsAsync(UniqueIndexName))
            return 0;

        long removed = 0;
        try
        {
            await ExecuteNonQueryAsync($"CREATE UNIQUE INDEX {UniqueIndexName} ON Files(FolderId, Name)");
        }
        catch (SqliteException)
        {
            // Existing duplicates - clean them up, then try again.
            MaintenanceStatusChanged?.Invoke("Removing duplicate entries from the index...");

            // A plain (FolderId, Name) index turns the grouping below into an ordered index walk
            // instead of a sort over every row, which is the difference between seconds and
            // minutes on a multi-million row index.
            await ExecuteNonQueryAsync("CREATE INDEX IF NOT EXISTS idx_files_folder_name ON Files(FolderId, Name)");

            removed = await ExecuteScalarLongAsync(@"
                SELECT COUNT(*) - COUNT(DISTINCT FolderId || '\' || Name) FROM Files");

            await ExecuteNonQueryAsync(@"
                DELETE FROM Files
                WHERE rowid NOT IN (SELECT MIN(rowid) FROM Files GROUP BY FolderId, Name)");

            await ExecuteNonQueryAsync("DROP INDEX IF EXISTS idx_files_folder_name");
            await ExecuteNonQueryAsync($"CREATE UNIQUE INDEX {UniqueIndexName} ON Files(FolderId, Name)");

            MaintenanceStatusChanged?.Invoke($"Removed {removed:N0} duplicate entries from the index");
        }

        // The old FolderId-only index is a strict prefix of the new one, so it can go.
        await ExecuteNonQueryAsync("DROP INDEX IF EXISTS idx_files_folder");
        await ExecuteNonQueryAsync("ANALYZE");

        return removed;
    }

    /// <summary>
    /// Drop folder rows nothing refers to any more.
    ///
    /// Deleting a folder removes its entries from Files but leaves the Folders row behind, and
    /// only the file watcher ever deletes anything - so on a machine that has been watched for a
    /// while this is pure accumulation from build output, temp trees and uninstalled programs.
    ///
    /// It is not harmless dead weight. The in-memory snapshot loads every folder path into one
    /// contiguous blob (MemoryIndexBuilder), and a search scans that entire blob once per term to
    /// find folder-path matches. Orphans make every keystroke scan further for results that no
    /// longer exist.
    ///
    /// Called from startup maintenance, right after <see cref="EnsureUniqueEntriesAsync"/>, which
    /// guarantees the (FolderId, Name) index the EXISTS check below seeks on.
    /// </summary>
    /// <returns>The number of folder rows removed.</returns>
    public async Task<long> PruneOrphanFoldersAsync()
    {
        using var dbLock = await LockAsync();

        var removed = await ExecuteScalarLongAsync(
            "SELECT COUNT(*) FROM Folders WHERE NOT EXISTS (SELECT 1 FROM Files WHERE Files.FolderId = Folders.Id)");
        if (removed == 0) return 0;

        await ExecuteNonQueryAsync(
            "DELETE FROM Folders WHERE NOT EXISTS (SELECT 1 FROM Files WHERE Files.FolderId = Folders.Id)");

        // The cache maps path -> id and has just been invalidated for every pruned path. Left in
        // place, the next file written under one of those paths would be stored against a folder
        // id that no longer exists, and would come back from a search with the wrong path.
        _folderCache.Clear();

        MaintenanceStatusChanged?.Invoke($"Removed {removed:N0} empty folder entries from the index");
        return removed;
    }

    /// <summary>
    /// Compact the database when a large share of it is free pages.
    ///
    /// Deleting rows only marks pages free inside the file, so after the duplicate clean-up the
    /// database still occupies everything it did when it was full of duplicates - a third of the
    /// file, in the case that motivated this. Best-effort: VACUUM needs the file to itself, so if
    /// another connection is busy with it this simply returns and the next startup tries again.
    /// </summary>
    /// <param name="wasteThreshold">Compact once this fraction of the file is not live data.</param>
    /// <param name="minimumBytesToReclaim">
    /// Never rewrite the database for less than this. Lowered by tests, which work with databases
    /// far smaller than a real index.
    /// </param>
    /// <returns>Bytes reclaimed, or 0 if nothing was done.</returns>
    public async Task<long> CompactIfFragmentedAsync(
        double wasteThreshold = 0.20,
        long minimumBytesToReclaim = 32L * 1024 * 1024)
    {
        using var dbLock = await LockAsync();

        var pageCount = await ExecuteScalarLongAsync("PRAGMA page_count");
        var pageSize = await ExecuteScalarLongAsync("PRAGMA page_size");
        var freeList = await ExecuteScalarLongAsync("PRAGMA freelist_count");
        if (pageCount <= 0 || pageSize <= 0) return 0;

        // Two different kinds of waste, and the clean-up leaves behind both of them:
        //   - free pages sitting inside the database, straight after rows are deleted;
        //   - a main file still bigger than the database it now holds, because a VACUUM in WAL
        //     mode compacts the database without handing the space back to the file system.
        // Checking only the first meant an already-compacted database kept a file twice the size
        // it needed. The write-ahead log is deliberately NOT counted here - it is working space
        // that comes and goes, not waste.
        var liveBytes = (pageCount - freeList) * pageSize;
        var mainFileBytes = MainFileBytes();
        var wasted = Math.Max(freeList * pageSize, mainFileBytes - liveBytes);

        if (wasted < minimumBytesToReclaim) return 0;
        if (mainFileBytes <= 0 || (double)wasted / mainFileBytes < wasteThreshold) return 0;

        var before = FootprintBytes();
        try
        {
            MaintenanceStatusChanged?.Invoke("Compacting the index...");

            await ExecuteNonQueryAsync("PRAGMA wal_checkpoint(TRUNCATE)");
            await ExecuteNonQueryAsync("VACUUM");

            // In WAL mode VACUUM writes the rebuilt database into the log; the file itself only
            // shrinks once that is checkpointed back. Without this the space is never returned
            // to the file system even though the database is now compact.
            await ExecuteNonQueryAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (SqliteException)
        {
            // Another connection holds the file - leave it for the next startup.
            return 0;
        }

        var reclaimed = before - FootprintBytes();
        if (reclaimed > 0)
            MaintenanceStatusChanged?.Invoke($"Index compacted - {reclaimed / (1024 * 1024):N0} MB reclaimed");

        return reclaimed > 0 ? reclaimed : 0;
    }

    /// <summary>Size of the database file itself, excluding the write-ahead log.</summary>
    private long MainFileBytes()
    {
        try
        {
            var info = new FileInfo(_dbPath);
            return info.Exists ? info.Length : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Everything the index occupies on disk, including the write-ahead log. Used to report how
    /// much was reclaimed: a compaction moves data between those files, so measuring only one of
    /// them gives a number that bears no relation to what the user gets back.
    /// </summary>
    private long FootprintBytes()
    {
        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var info = new FileInfo(_dbPath + suffix);
                if (info.Exists) total += info.Length;
            }
            catch { }
        }
        return total;
    }


    private async Task<bool> IndexExistsAsync(string name)
    {
        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name", _connection);
        cmd.Parameters.AddWithValue("@name", name);
        var result = await cmd.ExecuteScalarAsync();
        return result != null && Convert.ToInt64(result) > 0;
    }

    private async Task<long> ExecuteScalarLongAsync(string sql)
    {
        using var cmd = new SqliteCommand(sql, _connection);
        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt64(result);
    }
}
