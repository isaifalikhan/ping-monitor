using System.Globalization;
using NetWatch.Core.Metrics;

namespace NetWatch.Data.Metrics;

/// <summary>Describes one family of time-series tables (key column + value columns).</summary>
internal sealed record TableSpec(string Prefix, string KeyColumn, string[] Values);

internal static class TableSpecs
{
    public static readonly TableSpec Interface = new(
        "if",
        "InterfaceId",
        ["InBps", "OutBps", "InUtilPct", "OutUtilPct", "InErrPs", "OutErrPs", "InDiscPs", "OutDiscPs", "OperUp"]);

    public static readonly TableSpec Device = new(
        "dev",
        "DeviceId",
        ["Up", "RttMs", "LossPct", "CpuPct", "MemPct", "TempC"]);

    /// <summary>Generic single-value series (disk usage per volume, load, server throughput ...), keyed by Series.Id.</summary>
    public static readonly TableSpec Generic = new("gen", "SeriesId", ["Value"]);

    public static readonly TableSpec[] All = [Interface, Device, Generic];

    public static TableSpec? ByPrefix(string prefix) => All.FirstOrDefault(s => s.Prefix == prefix);
}

/// <summary>
/// Naming of date-sharded tables: {prefix}_raw_yyyyMMdd (daily), {prefix}_5m_yyyyMM (monthly),
/// {prefix}_1h_yyyy (yearly). Shard boundaries are UTC. Retention drops whole tables.
/// </summary>
public static class ShardNaming
{
    public static string Raw(string prefix, DateTime utc) =>
        $"{prefix}_raw_{utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    public static string FiveMinute(string prefix, DateTime utc) =>
        $"{prefix}_5m_{utc.ToString("yyyyMM", CultureInfo.InvariantCulture)}";

    public static string Hourly(string prefix, DateTime utc) =>
        $"{prefix}_1h_{utc.ToString("yyyy", CultureInfo.InvariantCulture)}";

    public static string For(MetricResolution resolution, string prefix, DateTime utc) => resolution switch
    {
        MetricResolution.Raw => Raw(prefix, utc),
        MetricResolution.FiveMinute => FiveMinute(prefix, utc),
        _ => Hourly(prefix, utc),
    };

    /// <summary>Start of the shard period that contains <paramref name="utc"/>.</summary>
    public static DateTime PeriodStart(MetricResolution resolution, DateTime utc) => resolution switch
    {
        MetricResolution.Raw => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
        MetricResolution.FiveMinute => new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => new DateTime(utc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public static DateTime NextPeriod(MetricResolution resolution, DateTime periodStart) => resolution switch
    {
        MetricResolution.Raw => periodStart.AddDays(1),
        MetricResolution.FiveMinute => periodStart.AddMonths(1),
        _ => periodStart.AddYears(1),
    };

    /// <summary>All shard table names overlapping [fromUtc, toUtc).</summary>
    public static IEnumerable<string> TablesInRange(MetricResolution resolution, string prefix, DateTime fromUtc, DateTime toUtc)
    {
        for (var p = PeriodStart(resolution, fromUtc); p < toUtc; p = NextPeriod(resolution, p))
        {
            yield return For(resolution, prefix, p);
        }
    }

    /// <summary>Parses a shard table name. Returns false for non-shard tables.</summary>
    public static bool TryParse(string table, out string prefix, out MetricResolution resolution, out DateTime periodStartUtc, out DateTime periodEndUtc)
    {
        prefix = string.Empty;
        resolution = MetricResolution.Raw;
        periodStartUtc = periodEndUtc = default;

        var parts = table.Split('_');
        if (parts.Length != 3)
        {
            return false;
        }

        prefix = parts[0];
        (resolution, var format) = parts[1] switch
        {
            "raw" => (MetricResolution.Raw, "yyyyMMdd"),
            "5m" => (MetricResolution.FiveMinute, "yyyyMM"),
            "1h" => (MetricResolution.Hourly, "yyyy"),
            _ => (MetricResolution.Raw, string.Empty),
        };

        if (format.Length == 0 ||
            !DateTime.TryParseExact(parts[2], format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start))
        {
            return false;
        }

        periodStartUtc = DateTime.SpecifyKind(start, DateTimeKind.Utc);
        periodEndUtc = NextPeriod(resolution, periodStartUtc);
        return true;
    }
}
