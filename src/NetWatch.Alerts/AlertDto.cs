using NetWatch.Core;
using NetWatch.Core.Entities;

namespace NetWatch.Alerts;

/// <summary>Alert as sent to the UI (REST and SignalR).</summary>
public sealed record AlertDto(
    long Id,
    int? RuleId,
    int? DeviceId,
    string? DeviceName,
    int? InterfaceId,
    Severity Severity,
    AlertState State,
    string Title,
    string Message,
    double? CurrentValue,
    bool AutoResolve,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    int OccurrenceCount,
    string? AckByUsername,
    DateTime? AckUtc,
    string? AckComment,
    DateTime? ResolvedUtc,
    DateTime? ClosedUtc,
    string? ClosedByUsername)
{
    public static AlertDto From(Alert a, string? deviceName) => new(
        a.Id, a.RuleId, a.DeviceId, deviceName, a.InterfaceId, a.Severity, a.State, a.Title, a.Message, a.CurrentValue,
        a.AutoResolve, a.FirstSeenUtc, a.LastSeenUtc, a.OccurrenceCount, a.AckByUsername, a.AckUtc, a.AckComment,
        a.ResolvedUtc, a.ClosedUtc, a.ClosedByUsername);
}

public sealed record AlertHistoryDto(DateTime TimestampUtc, string Action, string? Username, string? Comment);

/// <summary>Client-side SignalR event names for alerts.</summary>
public static class AlertEvents
{
    public const string Raised = "alertRaised";
    public const string Updated = "alertUpdated";
    public const string Resolved = "alertResolved";
}
