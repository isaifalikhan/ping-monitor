using NetWatch.Collectors.Servers;
using NetWatch.Core;

namespace NetWatch.Collectors.Vmware;

public sealed record HostReading(
    string MoRef,
    string Name,
    string? Cluster,
    string ConnectionState,
    string? PowerState,
    bool InMaintenance,
    string? OverallStatus,
    string? Vendor,
    string? Model,
    string? Product,
    int? CpuCores,
    long? CpuTotalMhz,
    long? CpuUsageMhz,
    long? MemTotalBytes,
    long? MemUsageBytes,
    DateTime? BootTimeUtc,
    int SensorWarnings,
    int SensorAlarms,
    IReadOnlyList<string> SensorProblems)
{
    public double? CpuPct => CpuTotalMhz is > 0 && CpuUsageMhz is { } u ? Math.Round(Math.Clamp(100.0 * u / CpuTotalMhz.Value, 0, 100), 1) : null;

    public double? MemPct => MemTotalBytes is > 0 && MemUsageBytes is { } u ? Math.Round(Math.Clamp(100.0 * u / MemTotalBytes.Value, 0, 100), 1) : null;
}

public sealed record VmReading(
    string MoRef,
    string Name,
    string? HostMoRef,
    string PowerState,
    string? GuestOs,
    string? IpAddress,
    string? GuestHostName,
    string? ToolsRunningStatus,
    string? ToolsVersionStatus,
    string? OverallStatus,
    int? NumCpu,
    int? MemoryMb,
    long? CpuUsageMhz,
    long? GuestMemoryUsageMb,
    long? BalloonedMb,
    long? CommittedBytes,
    long? UncommittedBytes,
    double? GuestDiskMaxPct,
    IReadOnlyList<string> DatastoreMoRefs,
    DateTime? BootTimeUtc,
    double? CpuPct)
{
    public bool PoweredOn => PowerState == "poweredOn";

    public double? MemPct => MemoryMb is > 0 && GuestMemoryUsageMb is { } u ? Math.Round(Math.Clamp(100.0 * u / MemoryMb.Value, 0, 100), 1) : null;
}

public sealed record DatastoreReading(
    string MoRef,
    string Name,
    string? Type,
    long CapacityBytes,
    long FreeBytes,
    long? UncommittedBytes,
    bool Accessible,
    string? MaintenanceMode,
    int HostCount,
    int VmCount)
{
    public double UsedPct => CapacityBytes <= 0 ? 0 : Math.Round(100.0 * (CapacityBytes - FreeBytes) / CapacityBytes, 1);
}

public sealed record SnapshotReading(string VmMoRef, string MoRef, string Name, string? Description, DateTime CreatedUtc, long? SizeBytes, bool IsCurrent);

public sealed record AlarmReading(string Key, string AlarmMoRef, string AlarmName, string EntityMoRef, string? EntityName, string Status, bool Acknowledged, DateTime TriggeredUtc);

public sealed record VmwareEventReading(long Key, DateTime CreatedUtc, string EventType, Severity Severity, string? UserName, string? EntityName, string Message);

/// <summary>What the vSphere collector should do in this poll.</summary>
/// <param name="EventBookmark">Last imported event key and time; null on first contact (no history is imported).</param>
public sealed record VmwarePollRequest(
    string Host,
    ManagementCredential Credential,
    string? PinnedThumbprint,
    bool TrustOnFirstUse,
    bool ImportEvents,
    bool ImportAlarms,
    bool CollectCpuReady,
    int MaxEvents,
    (long Key, DateTime TimeUtc)? EventBookmark,
    TimeSpan Timeout);

/// <summary>Everything one poll of a vCenter / ESXi API produced.</summary>
public sealed class VmwarePollData
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    /// <summary>"VirtualCenter" or "HostAgent" (standalone ESXi).</summary>
    public string? ApiType { get; set; }
    public string? Version { get; set; }
    public string? FullName { get; set; }
    public List<HostReading> Hosts { get; } = new();
    public List<VmReading> Vms { get; } = new();
    public List<DatastoreReading> Datastores { get; } = new();
    public List<SnapshotReading> Snapshots { get; } = new();

    /// <summary>Null when alarms were not read (so existing alarms are kept).</summary>
    public List<AlarmReading>? Alarms { get; set; }
    public List<VmwareEventReading> Events { get; } = new();

    /// <summary>New event bookmark (latest key and time) when events were read.</summary>
    public (long Key, DateTime TimeUtc)? NewEventBookmark { get; set; }

    /// <summary>CPU ready per VM MoRef, in percent per vCPU.</summary>
    public Dictionary<string, double> CpuReadyPct { get; } = new(StringComparer.Ordinal);

    /// <summary>Certificate thumbprint pinned on first use, to be stored on the device.</summary>
    public string? NewThumbprint { get; set; }
}

public interface IVmwareCollector
{
    Task<VmwarePollData> CollectAsync(VmwarePollRequest request, CancellationToken ct);
}
