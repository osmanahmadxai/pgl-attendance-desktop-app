using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PglAttendance.Core.Models;

namespace PglAttendance.Core.Data;

/// <summary>
/// SQLite-backed repository against the existing Prisma "RawAttendance" table.
/// Columns: id, rawData, isSynced, createdAt, retryCount, lastError.
/// Rows whose rawData starts with 'OPLOG' are device operation logs and are
/// excluded from listing, stats, and sync. rawData is deduplicated at insert
/// time (see InsertOrGetAsync) so one physical punch is exactly one row.
/// </summary>
public sealed class AttendanceRepository
{
    private readonly string _connectionString;

    public AttendanceRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public AttendanceRepository() : this(Paths.SqliteConnectionString) { }

    public void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""RawAttendance"" (
    ""id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    ""rawData"" TEXT NOT NULL,
    ""isSynced"" BOOLEAN NOT NULL DEFAULT false,
    ""createdAt"" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ""retryCount"" INTEGER NOT NULL DEFAULT 0,
    ""lastError"" TEXT
);
CREATE INDEX IF NOT EXISTS ""idx_rawattendance_synced"" ON ""RawAttendance""(""isSynced"");
CREATE INDEX IF NOT EXISTS ""idx_rawattendance_created"" ON ""RawAttendance""(""createdAt"");
";
        cmd.ExecuteNonQuery();

        // One-time migration for databases written by the old always-insert
        // code, which allowed the same physical punch to occupy several rows.
        // Keep the oldest row per rawData; if ANY copy was already synced,
        // mark the survivor synced so the punch is never re-sent. Then a
        // UNIQUE index makes the schema itself the dedup authority.
        using var migrate = conn.CreateCommand();
        migrate.CommandText = @"
UPDATE ""RawAttendance"" SET ""isSynced"" = 1
WHERE ""id"" IN (
    SELECT MIN(""id"") FROM ""RawAttendance""
    GROUP BY ""rawData"" HAVING COUNT(*) > 1 AND MAX(""isSynced"") = 1
);
DELETE FROM ""RawAttendance""
WHERE ""id"" NOT IN (SELECT MIN(""id"") FROM ""RawAttendance"" GROUP BY ""rawData"");
DROP INDEX IF EXISTS ""idx_rawattendance_rawdata"";
CREATE UNIQUE INDEX IF NOT EXISTS ""uq_rawattendance_rawdata"" ON ""RawAttendance""(""rawData"");
";
        migrate.ExecuteNonQuery();
    }

    /// <summary>
    /// Idempotent insert: an identical rawData line (device re-upload after a
    /// reboot/handshake, or a retried POST whose OK response was lost) maps to
    /// the row it already created instead of a new one. This is the ingestion
    /// half of the exactly-once guarantee — the same physical punch can never
    /// occupy two rows, so it can never be synced twice.
    /// </summary>
    public async Task<(RawAttendance Row, bool IsNew)> InsertOrGetAsync(string rawData)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO ""RawAttendance"" (""rawData"", ""isSynced"")
SELECT $r, 0
WHERE NOT EXISTS (SELECT 1 FROM ""RawAttendance"" WHERE ""rawData"" = $r);
SELECT ""id"", ""rawData"", ""isSynced"", ""createdAt"", ""retryCount"", ""lastError"", changes() AS ""inserted""
FROM ""RawAttendance"" WHERE ""rawData"" = $r ORDER BY ""id"" LIMIT 1;";
        cmd.Parameters.AddWithValue("$r", rawData);
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (!await rdr.ReadAsync())
            throw new InvalidOperationException("insert failed");
        var isNew = Convert.ToInt64(rdr["inserted"]) > 0;
        return (Map(rdr), isNew);
    }

    /// <summary>
    /// Current sync state of a row: true/false, or null if the row no longer
    /// exists (e.g. the user cleared the local database while it was queued).
    /// The engine calls this immediately before every POST so a record can
    /// never be sent twice no matter how many times it was queued.
    /// </summary>
    public async Task<bool?> IsSyncedAsync(long id)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""isSynced"" FROM ""RawAttendance"" WHERE ""id"" = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var result = await cmd.ExecuteScalarAsync();
        if (result is null || result is DBNull) return null;
        return Convert.ToBoolean(result);
    }

    public async Task MarkSyncedAsync(long id)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE ""RawAttendance"" SET ""isSynced"" = 1 WHERE ""id"" = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetLastErrorAsync(long id, string? error)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE ""RawAttendance"" SET ""lastError"" = $e WHERE ""id"" = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public sealed class Page
    {
        public List<ParsedAttendanceVm> Data { get; set; } = new();
        public long Total { get; set; }
        public int PageNumber { get; set; }
        public int Limit { get; set; }
        public int TotalPages { get; set; }
    }

    public async Task<Page> GetAttendanceAsync(int page, int limit, string filter, string? search = null)
    {
        if (page < 1) page = 1;
        if (limit < 1) limit = 10;
        var skip = (page - 1) * limit;

        // UI filter: only hide operation-log rows. We used to also require a
        // tab character but that hides any historical rows whose separator
        // isn't ASCII 0x09 (e.g. older devices/firmware that sent space- or
        // CRLF-separated fields). The desktop grid is read-only so showing
        // them is harmless and makes the dashboard actually represent what's
        // in the DB.
        var where = @"WHERE ""rawData"" NOT LIKE 'OPLOG%'";
        if (filter == "synced") where += @" AND ""isSynced"" = 1";
        else if (filter == "unsynced") where += @" AND ""isSynced"" = 0";

        // Global search across the whole table, not just the current page.
        // All-digit queries are treated as a user-id lookup (the record starts
        // with the user id) so searching "305" doesn't also match every date
        // and time containing those digits; anything else (dates, times) is a
        // substring match. LIKE is case-insensitive for ASCII.
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        string? q = null;
        if (hasSearch)
        {
            var trimmed = search!.Trim();
            q = EscapeLike(trimmed);
            where += IsAllDigits(trimmed)
                ? @" AND ""rawData"" LIKE $q || '%' ESCAPE '\'"
                : @" AND ""rawData"" LIKE '%' || $q || '%' ESCAPE '\'";
        }

        await using var conn = Open();

        long total;
        await using (var c = conn.CreateCommand())
        {
            c.CommandText = $@"SELECT COUNT(*) FROM ""RawAttendance"" {where};";
            if (hasSearch) c.Parameters.AddWithValue("$q", q);
            total = Convert.ToInt64(await c.ExecuteScalarAsync() ?? 0L);
        }

        var rows = new List<RawAttendance>();
        await using (var c = conn.CreateCommand())
        {
            c.CommandText = $@"
SELECT ""id"", ""rawData"", ""isSynced"", ""createdAt"", ""retryCount"", ""lastError""
FROM ""RawAttendance"" {where}
ORDER BY ""createdAt"" DESC
LIMIT $limit OFFSET $skip;";
            c.Parameters.AddWithValue("$limit", limit);
            c.Parameters.AddWithValue("$skip", skip);
            if (hasSearch) c.Parameters.AddWithValue("$q", q);
            await using var rdr = await c.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) rows.Add(Map(rdr));
        }

        var data = new List<ParsedAttendanceVm>(rows.Count);
        foreach (var row in rows)
        {
            var vm = Sync.AttendanceParser.ToVm(row);
            if (Sync.AttendanceParser.IsOplog(vm.UserId)) continue;
            // If the parser couldn't split the row at all (no tab), fall back
            // to surfacing the raw line in the UserId column so the UI still
            // shows that data did arrive.
            if (string.IsNullOrEmpty(vm.UserId) && string.IsNullOrEmpty(vm.DateTime))
            {
                vm.UserId = row.RawData;
            }
            data.Add(vm);
        }

        return new Page
        {
            Data = data,
            Total = total,
            PageNumber = page,
            Limit = limit,
            TotalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)limit),
        };
    }

    public sealed class Stats
    {
        public long Total { get; set; }
        public long Synced { get; set; }
        public long Unsynced { get; set; }
    }

    public async Task<Stats> GetStatsAsync()
    {
        await using var conn = Open();
        async Task<long> CountAsync(string extra)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = @$"SELECT COUNT(*) FROM ""RawAttendance""
WHERE ""rawData"" NOT LIKE 'OPLOG%' {extra};";
            return Convert.ToInt64(await c.ExecuteScalarAsync() ?? 0L);
        }
        var total = await CountAsync("");
        var synced = await CountAsync(@"AND ""isSynced"" = 1");
        var unsynced = await CountAsync(@"AND ""isSynced"" = 0");
        return new Stats { Total = total, Synced = synced, Unsynced = unsynced };
    }

    /// <summary>
    /// Returns the most recent N rows from RawAttendance with NO filtering —
    /// useful for diagnostics when device data arrives but doesn't show up in
    /// the grid because of the OPLOG / requires-tab filter the main query applies.
    /// Includes both the raw text and a snippet showing how it parses.
    /// </summary>
    public async Task<List<object>> GetRecentRawAsync(int limit)
    {
        if (limit < 1) limit = 1;
        if (limit > 1000) limit = 1000;
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT ""id"", ""rawData"", ""isSynced"", ""createdAt"", ""retryCount"", ""lastError""
FROM ""RawAttendance""
ORDER BY ""id"" DESC
LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        var result = new List<object>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var row = Map(rdr);
            var parsed = Sync.AttendanceParser.Parse(row.RawData);
            result.Add(new
            {
                id = row.Id,
                rawData = row.RawData,
                rawBytes = System.Text.Encoding.UTF8.GetByteCount(row.RawData),
                rawHasTab = row.RawData.Contains('\t'),
                isOplog = Sync.AttendanceParser.IsOplog(row.RawData),
                parsed = new { userId = parsed.UserId, datetime = parsed.DateTime, status = parsed.Status, verifyType = parsed.VerifyType },
                isSynced = row.IsSynced,
                createdAt = row.CreatedAt,
                retryCount = row.RetryCount,
                lastError = row.LastError,
            });
        }
        return result;
    }

    /// <summary>
    /// All unsynced non-OPLOG rows for "sync all", id asc. Final send order is
    /// decided by the engine's priority queue (oldest punch timestamp first).
    /// </summary>
    public async Task<List<(long Id, string RawData)>> GetUnsyncedForSyncAllAsync()
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT ""id"", ""rawData"" FROM ""RawAttendance""
WHERE ""isSynced"" = 0
  AND ""rawData"" NOT LIKE 'OPLOG%'
ORDER BY ""id"" ASC;";
        var rows = new List<(long, string)>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) rows.Add((rdr.GetInt64(0), rdr.GetString(1)));
        return rows;
    }

    public async Task<List<long>> GetAlreadySyncedAmongAsync(IEnumerable<long> ids)
    {
        var arr = new List<long>(ids);
        if (arr.Count == 0) return new();
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        var placeholders = string.Join(",", BuildPlaceholders(arr.Count, cmd, arr));
        cmd.CommandText = @$"SELECT ""id"" FROM ""RawAttendance"" WHERE ""id"" IN ({placeholders}) AND ""isSynced"" = 1;";
        var result = new List<long>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) result.Add(rdr.GetInt64(0));
        return result;
    }

    public async Task<List<(long Id, string RawData)>> GetUnsyncedByIdsAsync(IEnumerable<long> ids)
    {
        var arr = new List<long>(ids);
        if (arr.Count == 0) return new();
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        var placeholders = string.Join(",", BuildPlaceholders(arr.Count, cmd, arr));
        cmd.CommandText = @$"SELECT ""id"", ""rawData"" FROM ""RawAttendance""
WHERE ""id"" IN ({placeholders}) AND ""isSynced"" = 0;";
        var rows = new List<(long, string)>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) rows.Add((rdr.GetInt64(0), rdr.GetString(1)));
        return rows;
    }

    /// <summary>
    /// Deletes every row. Returns the number of rows removed. The
    /// autoincrement counter is deliberately NOT reset: ids must never be
    /// reused, otherwise a sync-queue entry that was in flight during the
    /// delete could bind to an unrelated new row with the same id and mark it
    /// synced without it ever being sent.
    /// </summary>
    public async Task<long> DeleteAllAsync()
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"DELETE FROM ""RawAttendance""; SELECT changes();";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>Escapes LIKE wildcards so user input is matched literally.</summary>
    private static string EscapeLike(string s)
        => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static bool IsAllDigits(string s)
    {
        foreach (var ch in s)
            if (ch < '0' || ch > '9') return false;
        return s.Length > 0;
    }

    private static IEnumerable<string> BuildPlaceholders(int count, SqliteCommand cmd, List<long> values)
    {
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var name = $"$i{i}";
            names.Add(name);
            cmd.Parameters.AddWithValue(name, values[i]);
        }
        return names;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    private static RawAttendance Map(IDataReader rdr) => new()
    {
        Id = rdr.GetInt64(rdr.GetOrdinal("id")),
        RawData = rdr.GetString(rdr.GetOrdinal("rawData")),
        IsSynced = rdr.GetBoolean(rdr.GetOrdinal("isSynced")),
        CreatedAt = rdr.GetDateTime(rdr.GetOrdinal("createdAt")),
        RetryCount = rdr.GetInt32(rdr.GetOrdinal("retryCount")),
        LastError = rdr.IsDBNull(rdr.GetOrdinal("lastError")) ? null : rdr.GetString(rdr.GetOrdinal("lastError")),
    };
}
