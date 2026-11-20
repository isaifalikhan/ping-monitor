namespace NetWatch.Core.Alerts;

/// <summary>Something the alert engine should evaluate. Produced by collectors.</summary>
public abstract record AlertSignal(int DeviceId, DateTime TimestampUtc);

/// <summary>A numeric measurement, evaluated by Threshold rules.</summary>
/// <param name="EntityName">Human-readable entity, e.g. the interface name. Null for device-level metrics.</param>
public sealed record MetricSignal(
    int DeviceId,
    int? InterfaceId,
    string MetricKey,
    string? Instance,
    double Value,
    DateTime TimestampUtc,
    string? EntityName = null,
    bool IsImportant = false) : AlertSignal(DeviceId, TimestampUtc);

/// <summary>A problem/ok state, evaluated by StateChange rules.</summary>
public sealed record StateSignal(
    int DeviceId,
    int? InterfaceId,
    string StateKey,
    string? Instance,
    bool IsProblem,
    string Description,
    DateTime TimestampUtc,
    string? EntityName = null,
    bool IsImportant = false) : AlertSignal(DeviceId, TimestampUtc);

/// <summary>A discrete event, evaluated by EventMatch rules.</summary>
public sealed record EventSignal(
    int DeviceId,
    string EventKey,
    string Message,
    DateTime TimestampUtc) : AlertSignal(DeviceId, TimestampUtc);

/// <summary>Entry point into the alert engine. Non-blocking.</summary>
public interface IAlertSignalSink
{
    void Publish(AlertSignal signal);
}

/// <summary>Index of open alerts per device, used to compute effective device health.</summary>
public interface IActiveAlertIndex
{
    /// <summary>Highest severity among open (Active/Acknowledged) alerts of a device, or null if none.</summary>
    Severity? GetWorstSeverity(int deviceId);

    /// <summary>Open alert count per severity.</summary>
    IReadOnlyDictionary<Severity, int> CountBySeverity();
}
