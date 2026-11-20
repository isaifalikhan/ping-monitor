namespace NetWatch.Core.Metrics;

/// <summary>One interface measurement (rates already derived from counters).</summary>
public readonly record struct InterfaceSample(
    int InterfaceId,
    long TsMs,
    double? InBps,
    double? OutBps,
    double? InUtilPct,
    double? OutUtilPct,
    double? InErrPs,
    double? OutErrPs,
    double? InDiscPs,
    double? OutDiscPs,
    bool? OperUp);

/// <summary>One device-level measurement (availability + health).</summary>
public readonly record struct DeviceSample(
    int DeviceId,
    long TsMs,
    bool Up,
    double? RttMs,
    double? LossPct,
    double? CpuPct,
    double? MemPct,
    double? TempC);

/// <summary>One value of a generic series (disk usage per volume, load, server throughput ...).</summary>
public readonly record struct GenericSample(int SeriesId, long TsMs, double Value);

/// <summary>A registered generic series: device + metric key + instance (e.g. volume "C:").</summary>
public sealed record SeriesInfo(int Id, int DeviceId, string Key, string Instance);

public enum MetricResolution
{
    Raw,
    FiveMinute,
    Hourly
}

/// <summary>Time series result. <see cref="SeriesPoint.Avg"/> and <see cref="SeriesPoint.Max"/> are aligned with <see cref="Columns"/>.</summary>
public sealed record SeriesData(MetricResolution Resolution, IReadOnlyList<string> Columns, IReadOnlyList<SeriesPoint> Points);

/// <summary>A point in a series. For raw data <see cref="Max"/> is null (avg == the raw value).</summary>
public sealed record SeriesPoint(long Ts, double?[] Avg, double?[]? Max);

/// <summary>Last raw counter values of an interface, used to derive rates (persisted across restarts).</summary>
public readonly record struct CounterSnapshot(
    long TsMs,
    ulong? InOctets,
    ulong? OutOctets,
    ulong? InErrors,
    ulong? OutErrors,
    ulong? InDiscards,
    ulong? OutDiscards,
    bool HighCapacity);
