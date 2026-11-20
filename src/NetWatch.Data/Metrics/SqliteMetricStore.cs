using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Metrics;

namespace NetWatch.Data.Metrics;

/// <summary>
/// SQLite implementation of <see cref="IMetricStore"/>.
/// <list type="bullet">
/// <item>Writes go through bounded in-memory queues drained by a single writer (SQLite allows one writer at a time),
/// in one transaction per batch with prepared statements.</item>
/// <item>Raw data is sharded into one table per UTC day, 5-minute rollups per month, hourly rollups per year,
/// so retention is a cheap DROP TABLE.</item>
/// <item>Reads use separate pooled connections; WAL mode lets them run concurrently with the writer.</item>
/// </list>
/// </summary>
public sealed class SqliteMetricStore : IMetricStore
{
    private const int QueueCapacity = 250_000;
    private const int MaxBatch = 50_000;

    private readonly string _connectionString;
    private readonly ILogger<SqliteMetricStore> _logger;
    private readonly Channel<InterfaceSample> _interfaceQueue;
    private readonly Channel<DeviceSample> _deviceQueue;
    private readonly ConcurrentDictionary<string, byte> _knownTables = new(StringComparer.Ordinal);
    private int _pending;
    private long _dropped;
    private volatile bool _initialised;

    public SqliteMetricStore(NetWatchPaths paths, ILogger<SqliteMetricStore> logger)
        : this(SqliteConnectionFactory.Build(paths.MetricsDb), logger)
    {
    }

    internal SqliteMetricStore(string connectionString, ILogger<SqliteMetricStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
        var options = new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };
        _interfaceQueue = Channel.CreateBounded<InterfaceSample>(options, _ => OnDropped());
        _deviceQueue = Channel.CreateBounded<DeviceSample>(options, _ => OnDropped());
    }

    public int PendingWrites => Volatile.Read(ref _pending);

    public long DroppedSamples => Interlocked.Read(ref _dropped);

    /// <summary>Creates fixed tables and loads the list of existing shards. Idempotent.</summary>
    public void Initialise()
    {
        using var connection = Open();
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS CounterState (
                InterfaceId INTEGER PRIMARY KEY,
                Ts INTEGER NOT NULL,
                InOctets INTEGER, OutOctets INTEGER,
                InErrors INTEGER, OutErrors INTEGER,
                InDiscards INTEGER, OutDiscards INTEGER,
                Hc INTEGER NOT NULL);
            """);
        RefreshKnownTables(connection);
        _initialised = true;
    }

    public void Enqueue(in InterfaceSample sample)
    {
        if (_interfaceQueue.Writer.TryWrite(sample))
        {
            Interlocked.Increment(ref _pending);
        }
    }

    public void Enqueue(in DeviceSample sample)
    {
        if (_deviceQueue.Writer.TryWrite(sample))
        {
            Interlocked.Increment(ref _pending);
        }
    }

    private void OnDropped()
    {
        Interlocked.Increment(ref _dropped);
        Interlocked.Decrement(ref _pending);
    }

    /// <summary>
    /// Single-writer loop. Flushes queued samples roughly every second until cancelled, then performs a final flush.
    /// </summary>
    public async Task RunWriterAsync(CancellationToken ct)
    {
        EnsureInitialised();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            FlushSafe();
        }

        // Final flush on shutdown so no polled data is lost.
        FlushSafe();
    }

    /// <summary>Writes everything currently queued. Exposed for tests.</summary>
    internal void Flush()
    {
        while (true)
        {
            var interfaces = Drain(_interfaceQueue.Reader);
            var devices = Drain(_deviceQueue.Reader);
            if (interfaces.Count == 0 && devices.Count == 0)
            {
                return;
            }

            WriteBatch(interfaces, devices);
            Interlocked.Add(ref _pending, -(interfaces.Count + devices.Count));
        }
    }

    private void FlushSafe()
    {
        try
        {
            Flush();
        }
        catch (Exception ex)
        {
            // Samples in the failed batch are lost; the next batch is attempted normally.
            _logger.LogError(ex, "Failed to write metric batch");

            // A rolled-back transaction may have undone CREATE TABLE statements: resync the table cache.
            try
            {
                using var connection = Open();
                RefreshKnownTables(connection);
            }
            catch (Exception refreshEx)
            {
                _logger.LogWarning(refreshEx, "Could not refresh metric table list");
            }
        }
    }

    private static List<T> Drain<T>(ChannelReader<T> reader)
    {
        var list = new List<T>();
        while (list.Count < MaxBatch && reader.TryRead(out var item))
        {
            list.Add(item);
        }

        return list;
    }

    private void WriteBatch(List<InterfaceSample> interfaces, List<DeviceSample> devices)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var commands = new Dictionary<string, SqliteCommand>(StringComparer.Ordinal);
        try
        {
            foreach (var s in interfaces)
            {
                var cmd = GetInsert(connection, tx, commands, TableSpecs.Interface, s.TsMs);
                Bind(cmd, s.InterfaceId, s.TsMs, s.InBps, s.OutBps, s.InUtilPct, s.OutUtilPct, s.InErrPs, s.OutErrPs, s.InDiscPs, s.OutDiscPs,
                    s.OperUp is null ? null : (s.OperUp.Value ? 1.0 : 0.0));
                cmd.ExecuteNonQuery();
            }

            foreach (var s in devices)
            {
                var cmd = GetInsert(connection, tx, commands, TableSpecs.Device, s.TsMs);
                Bind(cmd, s.DeviceId, s.TsMs, s.Up ? 1.0 : 0.0, s.RttMs, s.LossPct, s.CpuPct, s.MemPct, s.TempC);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        finally
        {
            foreach (var c in commands.Values)
            {
                c.Dispose();
            }
        }
    }

    private SqliteCommand GetInsert(SqliteConnection connection, SqliteTransaction tx, Dictionary<string, SqliteCommand> cache, TableSpec spec, long tsMs)
    {
        var table = ShardNaming.Raw(spec.Prefix, DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime);
        if (cache.TryGetValue(table, out var existing))
        {
            return existing;
        }

        EnsureTable(connection, tx, spec, table, rollup: false);
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        var columns = string.Join(", ", spec.Values);
        var parameters = string.Join(", ", spec.Values.Select((_, i) => $"$v{i}"));
        cmd.CommandText = $"INSERT OR REPLACE INTO {table} ({spec.KeyColumn}, Ts, {columns}) VALUES ($k, $ts, {parameters})";
        cmd.Parameters.Add("$k", SqliteType.Integer);
        cmd.Parameters.Add("$ts", SqliteType.Integer);
        for (var i = 0; i < spec.Values.Length; i++)
        {
            cmd.Parameters.Add($"$v{i}", SqliteType.Real);
        }

        cmd.Prepare();
        cache[table] = cmd;
        return cmd;
    }

    private static void Bind(SqliteCommand cmd, int key, long ts, params double?[] values)
    {
        cmd.Parameters[0].Value = key;
        cmd.Parameters[1].Value = ts;
        for (var i = 0; i < values.Length; i++)
        {
            cmd.Parameters[i + 2].Value = values[i].HasValue && double.IsFinite(values[i]!.Value) ? values[i]!.Value : DBNull.Value;
        }
    }

    private void EnsureTable(SqliteConnection connection, SqliteTransaction? tx, TableSpec spec, string table, bool rollup)
    {
        if (_knownTables.ContainsKey(table))
        {
            return;
        }

        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE IF NOT EXISTS {table} ({spec.KeyColumn} INTEGER NOT NULL, Ts INTEGER NOT NULL");
        foreach (var v in spec.Values)
        {
            if (rollup)
            {
                sb.Append($", {v}_avg REAL, {v}_max REAL");
            }
            else
            {
                sb.Append($", {v} REAL");
            }
        }

        if (rollup)
        {
            sb.Append(", N INTEGER NOT NULL");
        }

        sb.Append($", PRIMARY KEY ({spec.KeyColumn}, Ts)) WITHOUT ROWID;");
        Execute(connection, tx, sb.ToString());
        _knownTables[table] = 0;
    }

    // ------------------------------------------------------------------ queries

    public Task<SeriesData> QueryInterfaceAsync(int interfaceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        QueryAsync(TableSpecs.Interface, interfaceId, fromUtc, toUtc, ct);

    public Task<SeriesData> QueryDeviceAsync(int deviceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        QueryAsync(TableSpecs.Device, deviceId, fromUtc, toUtc, ct);

    /// <summary>Picks a resolution suited to the range: ≤26 h raw, ≤31 days 5-minute, otherwise hourly.</summary>
    public static MetricResolution ChooseResolution(DateTime fromUtc, DateTime toUtc)
    {
        var span = toUtc - fromUtc;
        if (span <= TimeSpan.FromHours(26))
        {
            return MetricResolution.Raw;
        }

        return span <= TimeSpan.FromDays(31) ? MetricResolution.FiveMinute : MetricResolution.Hourly;
    }

    private async Task<SeriesData> QueryAsync(TableSpec spec, int key, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        EnsureInitialised();
        if (toUtc <= fromUtc)
        {
            return new SeriesData(MetricResolution.Raw, spec.Values, Array.Empty<SeriesPoint>());
        }

        var resolution = ChooseResolution(fromUtc, toUtc);
        SeriesData result;
        do
        {
            result = await QueryResolutionAsync(spec, key, fromUtc, toUtc, resolution, ct);
            if (result.Points.Count > 0 || resolution == MetricResolution.Hourly)
            {
                break;
            }

            // Fall back to coarser data when finer data has already been purged.
            resolution = resolution == MetricResolution.Raw ? MetricResolution.FiveMinute : MetricResolution.Hourly;
        }
        while (true);

        return result;
    }

    private async Task<SeriesData> QueryResolutionAsync(TableSpec spec, int key, DateTime fromUtc, DateTime toUtc, MetricResolution resolution, CancellationToken ct)
    {
        var tables = ShardNaming.TablesInRange(resolution, spec.Prefix, fromUtc, toUtc).Where(_knownTables.ContainsKey).ToList();
        var points = new List<SeriesPoint>();
        if (tables.Count == 0)
        {
            return new SeriesData(resolution, spec.Values, points);
        }

        var rollup = resolution != MetricResolution.Raw;
        var selectColumns = rollup
            ? string.Join(", ", spec.Values.Select(v => $"{v}_avg").Concat(spec.Values.Select(v => $"{v}_max")))
            : string.Join(", ", spec.Values);
        var sql = string.Join(
            " UNION ALL ",
            tables.Select(t => $"SELECT Ts, {selectColumns} FROM {t} WHERE {spec.KeyColumn} = $k AND Ts >= $f AND Ts < $t"));
        sql += " ORDER BY Ts";

        await using var connection = Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$f", ToMs(fromUtc));
        cmd.Parameters.AddWithValue("$t", ToMs(toUtc));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var n = spec.Values.Length;
        while (await reader.ReadAsync(ct))
        {
            var avg = new double?[n];
            double?[]? max = rollup ? new double?[n] : null;
            for (var i = 0; i < n; i++)
            {
                avg[i] = reader.IsDBNull(i + 1) ? null : reader.GetDouble(i + 1);
                if (max is not null)
                {
                    max[i] = reader.IsDBNull(i + 1 + n) ? null : reader.GetDouble(i + 1 + n);
                }
            }

            points.Add(new SeriesPoint(reader.GetInt64(0), avg, max));
        }

        return new SeriesData(resolution, spec.Values, points);
    }

    // ------------------------------------------------------------------ rollups

    /// <summary>
    /// Aggregates raw data in [fromUtc, toUtc) into 5-minute buckets. The range must lie within one UTC day
    /// (5-minute buckets never span days, so callers split ranges at midnight).
    /// </summary>
    internal void RollupRawToFiveMinute(DateTime fromUtc, DateTime toUtc)
    {
        foreach (var spec in TableSpecs.All)
        {
            var src = ShardNaming.Raw(spec.Prefix, fromUtc);
            if (!_knownTables.ContainsKey(src))
            {
                continue;
            }

            var dst = ShardNaming.FiveMinute(spec.Prefix, fromUtc);
            RollupInto(spec, src, dst, fromUtc, toUtc, bucketMs: 300_000, fromRollup: false);
        }
    }

    /// <summary>Aggregates 5-minute data in [fromUtc, toUtc) into hourly buckets. Range must lie within one UTC month.</summary>
    internal void RollupFiveMinuteToHourly(DateTime fromUtc, DateTime toUtc)
    {
        foreach (var spec in TableSpecs.All)
        {
            var src = ShardNaming.FiveMinute(spec.Prefix, fromUtc);
            if (!_knownTables.ContainsKey(src))
            {
                continue;
            }

            var dst = ShardNaming.Hourly(spec.Prefix, fromUtc);
            RollupInto(spec, src, dst, fromUtc, toUtc, bucketMs: 3_600_000, fromRollup: true);
        }
    }

    private void RollupInto(TableSpec spec, string src, string dst, DateTime fromUtc, DateTime toUtc, long bucketMs, bool fromRollup)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        EnsureTable(connection, tx, spec, dst, rollup: true);

        var insertCols = string.Join(", ", spec.Values.Select(v => $"{v}_avg, {v}_max"));
        var selectCols = fromRollup
            ? string.Join(", ", spec.Values.Select(v => $"AVG({v}_avg), MAX({v}_max)"))
            : string.Join(", ", spec.Values.Select(v => $"AVG({v}), MAX({v})"));
        var count = fromRollup ? "SUM(N)" : "COUNT(*)";

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT OR REPLACE INTO {dst} ({spec.KeyColumn}, Ts, {insertCols}, N)
            SELECT {spec.KeyColumn}, (Ts / {bucketMs}) * {bucketMs} AS B, {selectCols}, {count}
            FROM {src}
            WHERE Ts >= $f AND Ts < $t
            GROUP BY {spec.KeyColumn}, B
            """;
        cmd.Parameters.AddWithValue("$f", ToMs(fromUtc));
        cmd.Parameters.AddWithValue("$t", ToMs(toUtc));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    internal string? GetMeta(string key)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Meta WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    internal void SetMeta(string key, string value)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Meta (Key, Value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ retention

    /// <summary>Drops shard tables whose whole period is older than the retention for their resolution.</summary>
    /// <returns>Names of dropped tables.</returns>
    public IReadOnlyList<string> ApplyRetention(RetentionSettings retention, DateTime nowUtc)
    {
        EnsureInitialised();
        var dropped = new List<string>();
        using var connection = Open();
        RefreshKnownTables(connection);
        foreach (var table in _knownTables.Keys.ToList())
        {
            if (!ShardNaming.TryParse(table, out var prefix, out var resolution, out _, out var periodEnd) || TableSpecs.ByPrefix(prefix) is null)
            {
                continue;
            }

            var keepDays = resolution switch
            {
                MetricResolution.Raw => retention.RawDays,
                MetricResolution.FiveMinute => retention.FiveMinuteDays,
                _ => retention.HourlyDays,
            };

            if (periodEnd <= nowUtc.AddDays(-keepDays))
            {
                Execute(connection, null, $"DROP TABLE IF EXISTS {table}");
                _knownTables.TryRemove(table, out _);
                dropped.Add(table);
            }
        }

        return dropped;
    }

    /// <summary>Approximate size of the metrics database file in bytes.</summary>
    public long DatabaseSizeBytes()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    // ------------------------------------------------------------------ counter state

    public async Task<IReadOnlyDictionary<int, CounterSnapshot>> LoadCounterStatesAsync(CancellationToken ct)
    {
        EnsureInitialised();
        var result = new Dictionary<int, CounterSnapshot>();
        await using var connection = Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT InterfaceId, Ts, InOctets, OutOctets, InErrors, OutErrors, InDiscards, OutDiscards, Hc FROM CounterState";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result[r.GetInt32(0)] = new CounterSnapshot(
                r.GetInt64(1), U(r, 2), U(r, 3), U(r, 4), U(r, 5), U(r, 6), U(r, 7), r.GetInt64(8) != 0);
        }

        return result;

        static ulong? U(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : unchecked((ulong)r.GetInt64(i));
    }

    public async Task SaveCounterStatesAsync(IReadOnlyCollection<KeyValuePair<int, CounterSnapshot>> states, CancellationToken ct)
    {
        EnsureInitialised();
        await using var connection = Open();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO CounterState (InterfaceId, Ts, InOctets, OutOctets, InErrors, OutErrors, InDiscards, OutDiscards, Hc)
            VALUES ($id, $ts, $a, $b, $c, $d, $e, $f, $hc)
            """;
        var p = new[] { "$id", "$ts", "$a", "$b", "$c", "$d", "$e", "$f", "$hc" }
            .Select(n => cmd.Parameters.Add(n, SqliteType.Integer))
            .ToArray();
        foreach (var (id, s) in states)
        {
            p[0].Value = id;
            p[1].Value = s.TsMs;
            p[2].Value = L(s.InOctets);
            p[3].Value = L(s.OutOctets);
            p[4].Value = L(s.InErrors);
            p[5].Value = L(s.OutErrors);
            p[6].Value = L(s.InDiscards);
            p[7].Value = L(s.OutDiscards);
            p[8].Value = s.HighCapacity ? 1 : 0;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        static object L(ulong? v) => v.HasValue ? unchecked((long)v.Value) : DBNull.Value;
    }

    // ------------------------------------------------------------------ helpers

    private void EnsureInitialised()
    {
        if (!_initialised)
        {
            Initialise();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void RefreshKnownTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = cmd.ExecuteReader();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var name = reader.GetString(0);
            seen.Add(name);
            _knownTables[name] = 0;
        }

        foreach (var name in _knownTables.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _knownTables.TryRemove(name, out _);
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
