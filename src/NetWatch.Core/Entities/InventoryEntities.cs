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
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public List<DeviceTag> Tags { get; set; } = new();
    public List<DeviceCredential> Credentials { get; set; } = new();
    public List<NetInterface> Interfaces { get; set; } = new();
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
