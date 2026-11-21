using NetWatch.Collectors.Inventory;
using NetWatch.Core;
using NetWatch.Core.Credentials;

namespace NetWatch.Collectors.Polling;

/// <summary>Decrypted SNMP credential for one poll.</summary>
public sealed record SnmpCredential(CredentialKind Kind, CredentialSecret Secret);

/// <summary>What to poll on a device in this cycle.</summary>
/// <param name="RefreshInterfaces">Re-read the interface table (names, speeds, aliases).</param>
/// <param name="RefreshInventory">Re-read vendor/model/serial/firmware.</param>
/// <param name="Server">Server collection (WinRM/WMI or SSH) for this cycle, or null.</param>
/// <param name="SnmpStorage">Read disk volumes from HOST-RESOURCES-MIB (servers monitored by SNMP only).</param>
public sealed record PollRequest(
    DeviceSnapshot Device,
    SnmpCredential? Snmp,
    bool RefreshInterfaces,
    bool RefreshInventory,
    int PingCount,
    int PingTimeoutMs,
    int SnmpTimeoutMs,
    int SnmpRetries,
    Servers.ServerPollRequest? Server = null,
    bool SnmpStorage = false,
    Vmware.VmwarePollRequest? Vmware = null,
    DomainControllers.DcPollRequest? Dc = null);

/// <summary>Raw interface counters and status from one poll.</summary>
public sealed record InterfaceCounters(
    int IfIndex,
    ulong? InOctets,
    ulong? OutOctets,
    ulong? InErrors,
    ulong? OutErrors,
    ulong? InDiscards,
    ulong? OutDiscards,
    int OperStatus,
    int AdminStatus,
    bool HighCapacity);

/// <summary>Interface table row (metadata).</summary>
public sealed record InterfaceMetadata(
    int IfIndex,
    string Name,
    string? Description,
    string? Alias,
    int IfType,
    long SpeedBps,
    string? Mac);

public sealed record SensorReading(SensorKind Kind, string SourceKey, string Name, double? Value, string? Unit, SensorState State);

public sealed record DeviceInventoryInfo(
    string? Vendor,
    string? Model,
    string? SerialNumber,
    string? Firmware,
    string? SysObjectId,
    string? SysName,
    string? SysDescription,
    string? Location);

/// <summary>Everything one poll of a device produced. Raw counters; rates are derived by the processor.</summary>
public sealed class DevicePollResult
{
    public required int DeviceId { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public bool PingOk { get; set; }
    public double? RttMs { get; set; }
    public double? LossPct { get; set; }

    /// <summary>Null when the device has no SNMP credential.</summary>
    public bool? SnmpOk { get; set; }
    public string? SnmpError { get; set; }

    /// <summary>sysUpTime in seconds.</summary>
    public long? UptimeSec { get; set; }
    public double? CpuPct { get; set; }
    public double? MemPct { get; set; }
    public List<InterfaceCounters> Interfaces { get; } = new();

    /// <summary>Set only when the interface table was refreshed this cycle.</summary>
    public List<InterfaceMetadata>? InterfaceMetadata { get; set; }
    public List<SensorReading> Sensors { get; } = new();

    /// <summary>Set only when inventory was refreshed this cycle.</summary>
    public DeviceInventoryInfo? Inventory { get; set; }

    /// <summary>Server facts (WinRM/WMI, SSH or SNMP storage), or null for network devices.</summary>
    public Servers.ServerPollData? Server { get; set; }

    /// <summary>vCenter / ESXi inventory and health, for devices with a vCenter credential.</summary>
    public Vmware.VmwarePollData? Vmware { get; set; }

    /// <summary>Domain controller security events, replication and clock, for DCs with a Windows credential.</summary>
    public DomainControllers.DcPollData? Dc { get; set; }
    public TimeSpan Duration { get; set; }
}

public interface IDevicePoller
{
    Task<DevicePollResult> PollAsync(PollRequest request, CancellationToken ct);
}
