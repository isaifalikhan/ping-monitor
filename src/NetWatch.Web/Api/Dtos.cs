using NetWatch.Core;
using NetWatch.Core.Metrics;

namespace NetWatch.Web.Api;

public sealed record DeviceListItem(
    int Id,
    string Name,
    string IpAddress,
    DeviceType Type,
    string? Vendor,
    string? Model,
    int? GroupId,
    string? GroupPath,
    DeviceStatus Status,
    DeviceStatus RawStatus,
    Severity? WorstAlert,
    bool MaintenanceMode,
    bool IsEnabled,
    DateTime? LastPollUtc,
    DateTime StatusSinceUtc,
    double? RttMs,
    double? LossPct,
    double? CpuPct,
    double? MemPct,
    IReadOnlyList<string> Tags,
    OsKind Os,
    string? MaintenanceReason,
    string? UnreachableVia);

public sealed record DeviceCredentialDto(CredentialPurpose Purpose, int CredentialProfileId, string? ProfileName);

public sealed record DeviceDetailDto(
    int Id,
    string Name,
    string IpAddress,
    string? Hostname,
    DeviceType Type,
    OsKind Os,
    string? Vendor,
    string? Model,
    string? SerialNumber,
    string? Firmware,
    string? SysObjectId,
    string? SysName,
    string? SysDescription,
    string? Location,
    int? GroupId,
    string? GroupPath,
    int PollIntervalSec,
    bool IsEnabled,
    bool MaintenanceMode,
    DateTime? MaintenanceUntilUtc,
    int? ParentDeviceId,
    string? ParentDeviceName,
    DeviceStatus Status,
    DeviceStatus RawStatus,
    Severity? WorstAlert,
    DateTime StatusSinceUtc,
    DateTime? LastPollUtc,
    long? UptimeSec,
    double? RttMs,
    double? LossPct,
    double? CpuPct,
    double? MemPct,
    double? TempC,
    string? Notes,
    IReadOnlyList<string> Tags,
    IReadOnlyList<DeviceCredentialDto> Credentials,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    IReadOnlyList<string> WatchedServices,
    string? SshHostKeyFingerprint,
    string? MaintenanceReason,
    string? UnreachableVia);

public sealed record DeviceInput(
    string? Name,
    string? IpAddress,
    string? Hostname,
    DeviceType Type,
    OsKind Os,
    string? Location,
    int? GroupId,
    int? PollIntervalSec,
    bool IsEnabled,
    int? ParentDeviceId,
    int? SnmpCredentialId,
    string? Notes,
    IReadOnlyList<string>? Tags,
    int? ManagementCredentialId = null,
    IReadOnlyList<string>? WatchedServices = null);

public sealed record MaintenanceRequest(IReadOnlyList<int> DeviceIds, bool Enabled, DateTime? UntilUtc);

public sealed record InterfaceDto(
    int Id,
    int DeviceId,
    int IfIndex,
    string Name,
    string? Description,
    string? Alias,
    int IfType,
    long SpeedBps,
    string? Mac,
    int AdminStatus,
    int OperStatus,
    DateTime? LastChangeUtc,
    bool IsImportant,
    bool IsMonitored,
    double? InBps,
    double? OutBps,
    double? InUtilPct,
    double? OutUtilPct,
    double? ErrorsPs);

public sealed record InterfaceUpdate(bool? IsImportant, bool? IsMonitored);

public sealed record BulkInterfaceUpdate(IReadOnlyList<int> InterfaceIds, bool? IsImportant, bool? IsMonitored);

public sealed record SensorDto(int Id, SensorKind Kind, string Name, double? LastValue, string? Unit, SensorState LastState, DateTime LastSeenUtc);

public sealed record DeviceEventDto(long Id, DateTime TimestampUtc, int? DeviceId, string? DeviceName, DeviceEventKind Kind, Severity Severity, string Message);

/// <summary>Chart-friendly time series: one array of timestamps (unix ms) plus one value array per column.</summary>
public sealed record SeriesDto(MetricResolution Resolution, IReadOnlyList<long> Ts, IReadOnlyDictionary<string, double?[]> Avg, IReadOnlyDictionary<string, double?[]>? Max)
{
    public static SeriesDto From(SeriesData data)
    {
        var ts = data.Points.Select(p => p.Ts).ToList();
        var avg = new Dictionary<string, double?[]>();
        Dictionary<string, double?[]>? max = data.Resolution == MetricResolution.Raw ? null : new Dictionary<string, double?[]>();
        for (var c = 0; c < data.Columns.Count; c++)
        {
            var column = char.ToLowerInvariant(data.Columns[c][0]) + data.Columns[c][1..];
            avg[column] = data.Points.Select(p => p.Avg[c]).ToArray();
            if (max is not null)
            {
                max[column] = data.Points.Select(p => p.Max?[c]).ToArray();
            }
        }

        return new SeriesDto(data.Resolution, ts, avg, max);
    }
}

public sealed record GroupDto(int Id, int? ParentId, string Name, GroupKind Kind, int SortOrder, int DeviceCount);

public sealed record GroupInput(string? Name, int? ParentId, GroupKind Kind, int SortOrder);

public sealed record CredentialDto(int Id, string Name, CredentialKind Kind, string DisplayHint, int UsedBy, DateTime UpdatedUtc);

public sealed record CredentialInput(
    string? Name,
    CredentialKind Kind,
    string? Community,
    string? Username,
    string? Password,
    string? Domain,
    SnmpAuthProtocol AuthProtocol,
    string? AuthPassword,
    SnmpPrivProtocol PrivProtocol,
    string? PrivPassword,
    string? ContextName,
    string? PrivateKey,

    /// <summary>On update: false keeps the stored secret and only renames the profile.</summary>
    bool ReplaceSecret = true);

public sealed record CredentialTestRequest(string? IpAddress);

public sealed record UserDto(int Id, string Username, string DisplayName, string? Email, UserRole Role, AuthSource AuthSource, bool IsEnabled, bool IsLocked, DateTime? LastLoginUtc, bool MustChangePassword, DateTime CreatedUtc);

public sealed record UserCreate(string? Username, string? DisplayName, string? Email, UserRole Role, string? Password, bool MustChangePassword = true);

public sealed record UserUpdate(string? DisplayName, string? Email, UserRole Role, bool IsEnabled);

public sealed record PasswordReset(string? Password, bool MustChangePassword = true);

public sealed record AuditDto(long Id, DateTime TimestampUtc, string Username, string Action, string? TargetType, string? TargetId, string? Details, string? ClientIp);

public sealed record RuleDto(
    int Id,
    string Name,
    string? Description,
    bool IsEnabled,
    bool IsBuiltIn,
    AlertRuleType RuleType,
    Severity Severity,
    Core.Alerts.RuleScope Scope,
    string Key,
    ComparisonOperator? Operator,
    double? Threshold,
    double? ClearThreshold,
    int DurationSec,
    DateTime UpdatedUtc,
    Core.Alerts.EventFilter Filter,
    int WindowSec,
    int CountThreshold,
    int? EscalateAfterMin);

public sealed record RuleInput(
    string? Name,
    string? Description,
    bool IsEnabled,
    AlertRuleType RuleType,
    Severity Severity,
    Core.Alerts.RuleScope? Scope,
    string? Key,
    ComparisonOperator? Operator,
    double? Threshold,
    double? ClearThreshold,
    int DurationSec,
    Core.Alerts.EventFilter? Filter = null,
    int WindowSec = 0,
    int CountThreshold = 0,
    int? EscalateAfterMin = null);

public sealed record RuleKeyDto(AlertRuleType RuleType, string Key, string DisplayName);

public sealed record IdsRequest(IReadOnlyList<long> Ids, string? Comment, bool IncludeOpen = false);

public sealed record CommentRequest(string? Comment);
