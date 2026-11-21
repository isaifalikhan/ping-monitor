namespace NetWatch.Core.Entities;

/// <summary>Node of the sites tree (e.g. Building &gt; Floor &gt; Rack).</summary>
public class DeviceGroup
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public DeviceGroup? Parent { get; set; }
    public List<DeviceGroup> Children { get; set; } = new();
    public string Name { get; set; } = string.Empty;
    public GroupKind Kind { get; set; } = GroupKind.Logical;
    public int SortOrder { get; set; }
}

/// <summary>
/// Reusable set of credentials. The secret part is DPAPI-encrypted in <see cref="SecretBlob"/> and is never
/// returned by the API. <see cref="DisplayHint"/> holds a non-secret description such as the user name.
/// </summary>
public class CredentialProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public CredentialKind Kind { get; set; }
    public string DisplayHint { get; set; } = string.Empty;
    public byte[] SecretBlob { get; set; } = Array.Empty<byte>();
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public class Device
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string? Hostname { get; set; }
    public DeviceType Type { get; set; } = DeviceType.Switch;
    public OsKind Os { get; set; } = OsKind.Unknown;
    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? Firmware { get; set; }
    public string? SysObjectId { get; set; }
    public string? SysName { get; set; }
    public string? SysDescription { get; set; }
    public string? Location { get; set; }
    public int? GroupId { get; set; }
    public DeviceGroup? Group { get; set; }

    /// <summary>Polling interval in seconds (minimum 15).</summary>
    public int PollIntervalSec { get; set; } = 60;
    public bool IsEnabled { get; set; } = true;
    public bool MaintenanceMode { get; set; }
    public DateTime? MaintenanceUntilUtc { get; set; }

    /// <summary>Upstream device (e.g. the core switch). Used for dependency-based alert suppression.</summary>
    public int? ParentDeviceId { get; set; }

    public DeviceStatus Status { get; set; } = DeviceStatus.Unknown;
    public DateTime StatusSinceUtc { get; set; }
    public DateTime? LastPollUtc { get; set; }

    /// <summary>Last sysUpTime in seconds, used to detect reboots.</summary>
    public long? LastUptimeSec { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// SHA-256 fingerprint of the SSH host key accepted on first connection (trust on first use). A different key
    /// later is refused until an administrator clears this value.
    /// </summary>
    public string? SshHostKeyFingerprint { get; set; }

    /// <summary>
    /// SHA-256 thumbprint of the vCenter/ESXi TLS certificate pinned on first connection when it is not issued by a
    /// trusted CA (typical for VMCA self-signed certificates). A different certificate is refused until cleared.
    /// </summary>
    public string? TlsThumbprint { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public List<DeviceTag> Tags { get; set; } = new();
    public List<DeviceCredential> Credentials { get; set; } = new();
    public List<NetInterface> Interfaces { get; set; } = new();
    public List<WatchedService> WatchedServices { get; set; } = new();
}

/// <summary>Latest server facts collected over WinRM/WMI, SSH or SNMP (one row per server).</summary>
public class ServerInfo
{
    public int DeviceId { get; set; }
    public ServerCollectionMethod Method { get; set; }
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public DateTime? LastBootUtc { get; set; }
    public bool? PendingReboot { get; set; }
    public DateTime? LastPatchUtc { get; set; }
    public string? LastPatchId { get; set; }
    public int? CpuCount { get; set; }
    public long? TotalMemoryBytes { get; set; }
    public double? Load1 { get; set; }
    public double? Load5 { get; set; }
    public double? Load15 { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime LastAttemptUtc { get; set; }

    /// <summary>Last collection error (null when the last attempt succeeded).</summary>
    public string? LastError { get; set; }

    /// <summary>Last event-log read error (event logs use a separate RPC channel and can fail independently).</summary>
    public string? EventLogError { get; set; }
}

/// <summary>A disk volume (Windows drive or Linux mount point) of a server.</summary>
public class Volume
{
    public int Id { get; set; }
    public int DeviceId { get; set; }

    /// <summary>"C:" on Windows, the mount point on Linux.</summary>
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public double UsedPct => TotalBytes <= 0 ? 0 : Math.Round(100.0 * (TotalBytes - FreeBytes) / TotalBytes, 1);
}

/// <summary>A Windows service or systemd unit that must be running on a server.</summary>
public class WatchedService
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public ServiceState LastState { get; set; } = ServiceState.Unknown;
    public string? StartMode { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? LastChangeUtc { get; set; }
}

public class DeviceTag
{
    public int DeviceId { get; set; }
    public string Tag { get; set; } = string.Empty;
}

public class DeviceCredential
{
    public int DeviceId { get; set; }
    public CredentialPurpose Purpose { get; set; }
    public int CredentialProfileId { get; set; }
    public CredentialProfile? CredentialProfile { get; set; }
}

/// <summary>A network interface (IF-MIB row) of a device.</summary>
public class NetInterface
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public int IfIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Alias { get; set; }
    public int IfType { get; set; }
    public long SpeedBps { get; set; }
    public string? Mac { get; set; }

    /// <summary>IF-MIB values: 1=up, 2=down, 3=testing, 5=dormant, 6=notPresent, 7=lowerLayerDown.</summary>
    public int AdminStatus { get; set; }
    public int OperStatus { get; set; }
    public DateTime? LastChangeUtc { get; set; }

    /// <summary>Only important ports (uplinks, server ports) raise port-down alerts.</summary>
    public bool IsImportant { get; set; }
    public bool IsMonitored { get; set; } = true;
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>Hardware sensor (temperature, fan, PSU) reported by the device.</summary>
public class Sensor
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public SensorKind Kind { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public double? LastValue { get; set; }
    public SensorState LastState { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>Device-related event (status change, reboot, port change...). Stored in events.db.</summary>
public class DeviceEvent
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int? DeviceId { get; set; }
    public DeviceEventKind Kind { get; set; }
    public Severity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>A syslog message received on UDP/TCP 514. Stored in events.db.</summary>
public class SyslogMessage
{
    public long Id { get; set; }
    public DateTime ReceivedUtc { get; set; }

    /// <summary>Timestamp from the message header, if it had a parseable one.</summary>
    public DateTime? MessageUtc { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public int? DeviceId { get; set; }

    /// <summary>RFC 5424 facility (0–23).</summary>
    public int Facility { get; set; }

    /// <summary>RFC 5424 severity: 0 emergency … 7 debug.</summary>
    public int SyslogSeverity { get; set; }
    public string? Hostname { get; set; }
    public string? AppName { get; set; }
    public string? ProcId { get; set; }
    public string? MsgId { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>An SNMP trap or inform received on UDP 162. Stored in events.db.</summary>
public class SnmpTrap
{
    public long Id { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public int? DeviceId { get; set; }

    /// <summary>"v1", "v2c" or "v3".</summary>
    public string Version { get; set; } = string.Empty;
    public string TrapOid { get; set; } = string.Empty;
    public string? TrapName { get; set; }
    public Severity Severity { get; set; }

    /// <summary>Human-readable one-line summary.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>JSON array of {oid, name?, value}.</summary>
    public string VarbindsJson { get; set; } = "[]";
}

/// <summary>A Critical/Error entry read from a server's System or Application event log. Stored in events.db.</summary>
public class WindowsEventEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int DeviceId { get; set; }
    public string LogName { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public int EventId { get; set; }

    /// <summary>Windows level: 1 Critical, 2 Error, 3 Warning, 4 Information.</summary>
    public int Level { get; set; }
    public long RecordId { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Last event-log record read per server and log, so reads resume without gaps or duplicates.</summary>
public class EventBookmark
{
    public int DeviceId { get; set; }
    public string LogName { get; set; } = string.Empty;
    public long LastRecordId { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
