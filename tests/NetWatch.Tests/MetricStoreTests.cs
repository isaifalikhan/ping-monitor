using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Metrics;
using NetWatch.Data;
using NetWatch.Data.Metrics;

namespace NetWatch.Tests;

public sealed class MetricStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"nw-metrics-{Guid.NewGuid():N}.db");
    private readonly SqliteMetricStore _store;

    public MetricStoreTests()
    {
        SqliteConnectionFactory.ConfigureDatabaseFile(_file);
        _store = new SqliteMetricStore(SqliteConnectionFactory.Build(_file), NullLogger<SqliteMetricStore>.Instance);
        _store.Initialise();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
            }
        }
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Fact]
    public void ShardNames()
    {
        var t = new DateTime(2026, 11, 18, 23, 59, 0, DateTimeKind.Utc);
        Assert.Equal("if_raw_20261118", ShardNaming.Raw("if", t));
        Assert.Equal("dev_5m_202611", ShardNaming.FiveMinute("dev", t));
        Assert.Equal("if_1h_2026", ShardNaming.Hourly("if", t));

        Assert.True(ShardNaming.TryParse("if_5m_202602", out var prefix, out var res, out var start, out var end));
        Assert.Equal("if", prefix);
        Assert.Equal(MetricResolution.FiveMinute, res);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), end);
        Assert.False(ShardNaming.TryParse("CounterState", out _, out _, out _, out _));
    }

    [Fact]
    public void TablesInRange_SpansDays()
    {
        var tables = ShardNaming.TablesInRange(MetricResolution.Raw, "if",
            new DateTime(2026, 11, 17, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 19, 1, 0, 0, DateTimeKind.Utc)).ToList();
        Assert.Equal(["if_raw_20261117", "if_raw_20261118", "if_raw_20261119"], tables);
    }

    [Theory]
    [InlineData(1, MetricResolution.Raw)]
    [InlineData(24, MetricResolution.Raw)]
    [InlineData(24 * 7, MetricResolution.FiveMinute)]
    [InlineData(24 * 90, MetricResolution.Hourly)]
    public void ChooseResolution(int hours, MetricResolution expected)
    {
        var to = DateTime.UtcNow;
        Assert.Equal(expected, SqliteMetricStore.ChooseResolution(to.AddHours(-hours), to));
    }

    [Fact]
    public async Task WriteQueryAndRollup()
    {
        var start = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10; i++)
        {
            var ts = Ms(start.AddMinutes(i));
            _store.Enqueue(new InterfaceSample(5, ts, 100 * (i + 1), 50, 10, 5, 0, 0, 0, 0, true));
            _store.Enqueue(new DeviceSample(9, ts, true, 2.5, 0, 40 + i, 60, null));
        }

        _store.Flush();
        Assert.Equal(0, _store.PendingWrites);

        var raw = await _store.QueryInterfaceAsync(5, start, start.AddHours(1), CancellationToken.None);
        Assert.Equal(MetricResolution.Raw, raw.Resolution);
        Assert.Equal(10, raw.Points.Count);
        Assert.Equal(1000, raw.Points[^1].Avg[0]);

        // Two 5-minute buckets: 10:00–10:05 and 10:05–10:10.
        _store.RollupRawToFiveMinute(start, start.AddMinutes(10));
        var fiveMin = await _store.QueryDeviceAsync(9, start.AddDays(-3), start.AddDays(3), CancellationToken.None);
        Assert.Equal(MetricResolution.FiveMinute, fiveMin.Resolution);
        Assert.Equal(2, fiveMin.Points.Count);
        var cpu = Array.IndexOf(fiveMin.Columns.ToArray(), "CpuPct");
        Assert.Equal(42, fiveMin.Points[0].Avg[cpu]);
        Assert.Equal(44, fiveMin.Points[0].Max![cpu]);

        _store.RollupFiveMinuteToHourly(start, start.AddHours(1));
        var hourly = await _store.QueryDeviceAsync(9, start.AddDays(-60), start.AddDays(1), CancellationToken.None);
        Assert.Equal(MetricResolution.Hourly, hourly.Resolution);
        Assert.Single(hourly.Points);
        Assert.Equal(49, hourly.Points[0].Max![cpu]);
    }

    [Fact]
    public async Task QueryFallsBackToRollupWhenRawIsGone()
    {
        var start = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc);
        _store.Enqueue(new DeviceSample(1, Ms(start), true, 1, 0, 10, 10, null));
        _store.Flush();
        _store.RollupRawToFiveMinute(start, start.AddMinutes(5));

        var dropped = _store.ApplyRetention(new RetentionSettings { RawDays = 7, FiveMinuteDays = 3650, HourlyDays = 3650 }, start.AddDays(30));
        Assert.Contains("dev_raw_20260105", dropped);

        var result = await _store.QueryDeviceAsync(1, start.AddMinutes(-30), start.AddMinutes(30), CancellationToken.None);
        Assert.Equal(MetricResolution.FiveMinute, result.Resolution);
        Assert.Single(result.Points);
    }

    [Fact]
    public async Task CounterStatesRoundTrip()
    {
        var snapshot = new CounterSnapshot(123, ulong.MaxValue - 1, 5, null, 2, 3, 4, true);
        await _store.SaveCounterStatesAsync([new KeyValuePair<int, CounterSnapshot>(42, snapshot)], CancellationToken.None);
        var loaded = await _store.LoadCounterStatesAsync(CancellationToken.None);
        Assert.Equal(snapshot, loaded[42]);
    }

    [Fact]
    public void RollupHelpers()
    {
        var t = new DateTime(2026, 11, 18, 10, 7, 31, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 18, 10, 5, 0, DateTimeKind.Utc), MetricRollupService.Floor(t, TimeSpan.FromMinutes(5)));
        var parts = MetricRollupService.SplitAt(
            new DateTime(2026, 11, 18, 23, 50, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 19, 0, 10, 0, DateTimeKind.Utc),
            d => d.Date.AddDays(1)).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal(new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc), parts[0].To);
    }
}
