using Microsoft.Data.Sqlite;

namespace AnythingSearch.Services.Search.Memory;

/// <summary>
/// Loads a <see cref="MemoryFileIndex"/> snapshot out of the SQLite database.
///
/// This is the only place that reads the whole Files table, and it does so exactly once per
/// snapshot (a few seconds for 3.3 million rows) instead of once per keystroke. It deliberately
/// opens its OWN read-only connection rather than borrowing the shared one, so building a snapshot
/// never blocks the file watcher, the catch-up pass or a search running on the old snapshot -
/// WAL mode lets readers and the writer proceed concurrently.
/// </summary>
public static class MemoryIndexBuilder
{
    /// <summary>
    /// Build a snapshot from the database at <paramref name="databasePath"/>.
    /// <paramref name="progress"/> reports the number of rows loaded so far.
    /// </summary>
    public static MemoryFileIndex Build(
        string databasePath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        // Pooling=False matters: Microsoft.Data.Sqlite pools connections by default, so a disposed
        // connection keeps the database file open in the background. A lingering reader stops
        // SQLite from taking the exclusive lock that VACUUM needs to hand disk space back, which
        // silently defeated the index compaction in FileDatabase.CompactIfFragmentedAsync.
        using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Cache=Private;Pooling=False");
        connection.Open();

        // A sequential read of every row, so a large page cache buys nothing and would only add
        // to the process footprint next to the index being built.
        Execute(connection, "PRAGMA temp_store = MEMORY");
        Execute(connection, "PRAGMA cache_size = -8000");        // 8 MB
        Execute(connection, "PRAGMA mmap_size = 134217728");     // 128 MB

        // Row counts AND the exact folded size of each blob, one scan per table.
        //
        // The byte totals are what let PackedStringTable.Builder allocate its blob once and never
        // grow it. They used to be guessed - 24 bytes per name, 96 per folder path - and a guess
        // that lands even slightly low is expensive out of all proportion: on a 1.4 million entry
        // index the folder table needed 17.3 MB against the 17.1 MB guess, so the builder doubled
        // to 34.2 MB to fit the last few hundred bytes and then copied back down, leaving two
        // large-object-heap arrays behind to retain one. LENGTH(CAST(x AS BLOB)) is the UTF-8 byte
        // count, which is exactly what the blob stores; the +Count is the NUL after each entry.
        //
        // Cost is about 130 ms against the 1.7 s the row read itself takes, and it is paid on a
        // background thread while searches continue on the previous snapshot.
        var (fileCount, nameBytes) = CountAndBytes(connection, "Name", "Files");
        var (folderCount, folderBytes) = CountAndBytes(connection, "Path", "Folders");
        long maxFolderId = Scalar(connection, "SELECT IFNULL(MAX(Id), 0) FROM Folders");

        // Folder ids are assigned by SQLite and can have gaps, so map id -> dense table index.
        var folderIndexById = new int[maxFolderId + 2];
        Array.Fill(folderIndexById, -1);

        var folderTable = new PackedStringTable.Builder(folderCount + 1, BlobSize(folderBytes, folderCount + 1));
        using (var cmd = new SqliteCommand("SELECT Id, Path FROM Folders", connection))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                long id = reader.GetInt64(0);
                if (id < 0 || id >= folderIndexById.Length) continue;
                folderIndexById[id] = folderTable.Add(reader.IsDBNull(1) ? "" : reader.GetString(1));
            }
        }

        // Unknown folder id (a row written between the two queries) falls back to a single empty
        // entry so the file is still searchable by name instead of being dropped.
        int unknownFolder = folderTable.Add("");
        var folders = folderTable.Build();

        var names = new PackedStringTable.Builder(fileCount, BlobSize(nameBytes, fileCount));
        var folderOf = new int[Math.Max(1, fileCount)];
        var sizes = new long[Math.Max(1, fileCount)];
        var modified = new int[Math.Max(1, fileCount)];
        var isFolderBits = new ulong[(Math.Max(1, fileCount) >> 6) + 1];

        int count = 0;
        using (var cmd = new SqliteCommand(
                   "SELECT Name, FolderId, Size, Modified, IsFolder FROM Files", connection))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if ((count & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(count);
                }

                if (count == folderOf.Length) Grow(ref folderOf, ref sizes, ref modified, ref isFolderBits);

                names.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));

                long folderId = reader.IsDBNull(1) ? -1 : reader.GetInt64(1);
                folderOf[count] = folderId >= 0 && folderId < folderIndexById.Length && folderIndexById[folderId] >= 0
                    ? folderIndexById[folderId]
                    : unknownFolder;

                sizes[count] = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                modified[count] = MemoryFileIndex.PackModified(SafeDate(reader, 3));

                if (!reader.IsDBNull(4) && reader.GetInt32(4) == 1)
                    isFolderBits[count >> 6] |= 1UL << (count & 63);

                count++;
            }
        }

        progress?.Report(count);

        return new MemoryFileIndex(
            names.Build(), folders,
            Trim(folderOf, count), Trim(sizes, count), Trim(modified, count),
            isFolderBits, count);
    }

    /// <summary>
    /// Blob bytes to reserve for <paramref name="count"/> entries totalling <paramref name="bytes"/>
    /// stored UTF-8 bytes: the entries, their NUL separators, and a small margin.
    ///
    /// The margin exists because folding is not always length-preserving. ASCII folds byte for
    /// byte, but a non-ASCII entry is lowercased with the invariant culture before it is encoded,
    /// and a few characters get longer when they are lowercased - Turkish dotted capital I is one
    /// UTF-8 sequence up and two down. Those entries are rare, so a fraction of a percent plus a
    /// fixed floor covers a realistic drive while still being far tighter than the old guess. The
    /// grow path stays in place for the case it does not.
    /// </summary>
    private static long BlobSize(long bytes, long count) =>
        bytes + count + Math.Max(64 * 1024, bytes / 256);

    /// <summary>Row count and total stored UTF-8 bytes of one text column, in a single scan.</summary>
    private static (int Count, long Bytes) CountAndBytes(SqliteConnection connection, string column, string table)
    {
        using var cmd = new SqliteCommand(
            $"SELECT COUNT(*), IFNULL(SUM(LENGTH(CAST({column} AS BLOB))), 0) FROM {table}", connection);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0);
        return ((int)Math.Min(int.MaxValue, reader.GetInt64(0)), reader.GetInt64(1));
    }

    private static DateTime SafeDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return DateTime.MinValue;
        long ticks = reader.GetInt64(ordinal);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return DateTime.MinValue;
        return new DateTime(ticks);
    }

    private static void Grow(ref int[] folderOf, ref long[] sizes, ref int[] modified, ref ulong[] isFolderBits)
    {
        int size = folderOf.Length * 2;
        Array.Resize(ref folderOf, size);
        Array.Resize(ref sizes, size);
        Array.Resize(ref modified, size);
        Array.Resize(ref isFolderBits, (size >> 6) + 1);
    }

    /// <summary>
    /// Shrink a parallel array to the rows actually read, but only when the slack is worth a copy.
    ///
    /// The arrays are sized from COUNT(*), so they normally come out exact and this does nothing.
    /// When rows were deleted between the count and the read the slack is a handful of entries,
    /// and resizing then would allocate a second copy of an array that is megabytes long to
    /// reclaim bytes. Nothing reads past <c>count</c>: MemoryFileIndex takes the count separately
    /// and indexes below it.
    /// </summary>
    private static T[] Trim<T>(T[] array, int count)
    {
        const int SlackThreshold = 64 * 1024;
        if (array.Length - count <= SlackThreshold) return array;
        Array.Resize(ref array, count);
        return array;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = new SqliteCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = new SqliteCommand(sql, connection);
        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
    }
}
