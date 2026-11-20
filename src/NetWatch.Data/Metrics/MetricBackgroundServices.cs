using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NetWatch.Data.Metrics;

/// <summary>Runs the single metric writer loop.</summary>
public sealed class MetricWriterService : BackgroundService
{
    private readonly SqliteMetricStore _store;

    public MetricWriterService(SqliteMetricStore store)
    {
        _store = store;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => _store.RunWriterAsync(stoppingToken), CancellationToken.None);
}

/// <summary>
/// Builds 5-minute rollups from raw data and hourly rollups from 5-minute data. Progress is stored in the
/// Meta table so the job catches up after downtime and never double-counts (INSERT OR REPLACE is idempotent).
/// </summary>
public sealed class MetricRollupService : BackgroundService
{
    internal const string FiveMinuteKey = "rollup.5m.lastBucketEnd";
    internal const string HourlyKey = "rollup.1h.lastBucketEnd";

    // Wait this long after a bucket closes before rolling it up, so late writes are included.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(90);

    private readonly SqliteMetricStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<MetricRollupService> _logger;

    public MetricRollupService(SqliteMetricStore store, TimeProvider time, ILogger<MetricRollupService> logger)
    {
        _store = store;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave the synchronous start-up path immediately so host start-up is not blocked by DB work.
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _time);
        do
        {
            try
            {
                RunOnce(_time.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metric rollup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Processes all closed buckets up to <paramref name="nowUtc"/>.</summary>
    internal void RunOnce(DateTime nowUtc)
    {
        var fiveMinuteEnd = Floor(nowUtc - SettleDelay, TimeSpan.FromMinutes(5));
        var fiveStart = ReadMarker(FiveMinuteKey) ?? fiveMinuteEnd.AddHours(-2);
        if (fiveStart < fiveMinuteEnd)
        {
            foreach (var (from, to) in SplitAt(fiveStart, fiveMinuteEnd, d => d.Date.AddDays(1)))
            {
                _store.RollupRawToFiveMinute(from, to);
            }

            WriteMarker(FiveMinuteKey, fiveMinuteEnd);
        }

        // Hourly rollups are built from 5-minute data, so they lag until the hour's 5-minute buckets exist.
        var hourEnd = Floor(fiveMinuteEnd, TimeSpan.FromHours(1));
        var hourStart = ReadMarker(HourlyKey) ?? hourEnd.AddHours(-2);
        if (hourStart < hourEnd)
        {
            foreach (var (from, to) in SplitAt(hourStart, hourEnd, d => new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)))
            {
                _store.RollupFiveMinuteToHourly(from, to);
            }

            WriteMarker(HourlyKey, hourEnd);
        }
    }

    private DateTime? ReadMarker(string key)
    {
        var value = _store.GetMeta(key);
        return value is not null && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : null;
    }

    private void WriteMarker(string key, DateTime utc) =>
        _store.SetMeta(key, SqliteMetricStore.ToMs(utc).ToString(CultureInfo.InvariantCulture));

    internal static DateTime Floor(DateTime utc, TimeSpan step) =>
        new(utc.Ticks - (utc.Ticks % step.Ticks), DateTimeKind.Utc);

    /// <summary>Splits [from, to) at period boundaries given by <paramref name="nextBoundary"/>.</summary>
    internal static IEnumerable<(DateTime From, DateTime To)> SplitAt(DateTime from, DateTime to, Func<DateTime, DateTime> nextBoundary)
    {
        var cursor = from;
        while (cursor < to)
        {
            var boundary = nextBoundary(cursor);
            var end = boundary < to ? boundary : to;
            yield return (cursor, end);
            cursor = end;
        }
    }
}
