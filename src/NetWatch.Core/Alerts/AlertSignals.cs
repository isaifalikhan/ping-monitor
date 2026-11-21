namespace NetWatch.Core.Alerts;

/// <summary>Something the alert engine should evaluate. Produced by collectors and receivers.</summary>
public abstract record AlertSignal(int DeviceId, DateTime TimestampUtc);

/// <summary>A numeric measurement, evaluated by Threshold (and Absence) rules.</summary>
/// <param name="EntityName">Human-readable entity, e.g. the interface or volume name. Null for device-level metrics.</param>
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

/// <summary>A discrete event, evaluated by EventMatch, CountInWindow and Absence rules.</summary>
/// <param name="Severity">Severity of the source event (syslog/Windows level, trap type), used by event filters.</param>
/// <param name="Source">Syslog app name, event-log provider or trap name.</param>
/// <param name="EventId">Windows event ID.</param>
/// <param name="Oid">SNMP trap OID.</param>
/// <param name="Fields">Named values (e.g. targetUser, sourceIp, group, offHours) for field filters and per-value counting.</param>
public sealed record EventSignal(
    int DeviceId,
    string EventKey,
    string Message,
    DateTime TimestampUtc,
    Severity Severity = Severity.Info,
    string? Source = null,
    int? EventId = null,
    string? Oid = null,
    IReadOnlyDictionary<string, string>? Fields = null) : AlertSignal(DeviceId, TimestampUtc);

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

/// <summary>
/// Dependency information for alert suppression: when an upstream device (the parent chain) is unreachable,
/// alerts for devices behind it are suppressed because they are a consequence, not a cause.
/// </summary>
public interface IDependencyStatus
{
    /// <summary>Name of the nearest unreachable upstream device of <paramref name="deviceId"/>, or null if all are reachable.</summary>
    string? FindUnreachableUpstream(int deviceId);
}
