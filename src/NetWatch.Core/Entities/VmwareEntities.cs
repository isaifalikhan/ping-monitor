namespace NetWatch.Core.Entities;

// VMware inventory as last collected from a vCenter (or a standalone ESXi host). Rows belong to the
// "source" device (Type VCenter or Hypervisor with a vCenter credential) and are keyed by the vSphere managed
// object reference (MoRef), which is stable for the life of the object.

/// <summary>An ESXi host.</summary>
public class VmwareHost
{
    public int Id { get; set; }
    public int SourceDeviceId { get; set; }
    public string MoRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Cluster { get; set; }

    /// <summary>connected, disconnected, notResponding.</summary>
    public string ConnectionState { get; set; } = string.Empty;
    public string? PowerState { get; set; }
    public bool InMaintenance { get; set; }

    /// <summary>gray, green, yellow, red.</summary>
    public string? OverallStatus { get; set; }
    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public string? Product { get; set; }
    public int? CpuCores { get; set; }
    public long? CpuTotalMhz { get; set; }
    public long? CpuUsageMhz { get; set; }
    public long? MemTotalBytes { get; set; }
    public long? MemUsageBytes { get; set; }
    public DateTime? BootTimeUtc { get; set; }
    public int SensorWarnings { get; set; }
    public int SensorAlarms { get; set; }

    /// <summary>Matching device in the NetWatch inventory (same IP or name), if any.</summary>
    public int? DeviceId { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>A virtual machine.</summary>
public class VmwareVm
{
    public int Id { get; set; }
    public int SourceDeviceId { get; set; }
    public string MoRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? HostMoRef { get; set; }
    public string? HostName { get; set; }

    /// <summary>poweredOn, poweredOff, suspended.</summary>
    public string PowerState { get; set; } = string.Empty;
    public string? GuestOs { get; set; }
    public string? IpAddress { get; set; }
    public string? GuestHostName { get; set; }

    /// <summary>guestToolsRunning, guestToolsNotRunning, guestToolsExecutingScripts.</summary>
    public string? ToolsRunningStatus { get; set; }

    /// <summary>guestToolsCurrent, guestToolsNeedUpgrade, guestToolsNotInstalled, guestToolsUnmanaged ...</summary>
    public string? ToolsVersionStatus { get; set; }
    public string? OverallStatus { get; set; }
    public int? NumCpu { get; set; }
    public int? MemoryMb { get; set; }
    public double? CpuPct { get; set; }
    public double? CpuReadyPct { get; set; }
    public double? MemPct { get; set; }
    public long? BalloonedMb { get; set; }
    public long? CommittedBytes { get; set; }
    public long? UncommittedBytes { get; set; }

    /// <summary>Fullest guest file system in percent (from VMware Tools).</summary>
    public double? GuestDiskMaxPct { get; set; }

    /// <summary>Comma-separated datastore names.</summary>
    public string? Datastores { get; set; }
    public int SnapshotCount { get; set; }
    public DateTime? BootTimeUtc { get; set; }

    /// <summary>Matching device in the NetWatch inventory (same IP or name), if any.</summary>
    public int? DeviceId { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>A datastore.</summary>
public class VmwareDatastore
{
    public int Id { get; set; }
    public int SourceDeviceId { get; set; }
    public string MoRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public long CapacityBytes { get; set; }
    public long FreeBytes { get; set; }
    public long? UncommittedBytes { get; set; }
    public bool Accessible { get; set; } = true;
    public string? MaintenanceMode { get; set; }
    public int HostCount { get; set; }
    public int VmCount { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public double UsedPct => CapacityBytes <= 0 ? 0 : Math.Round(100.0 * (CapacityBytes - FreeBytes) / CapacityBytes, 1);
}

/// <summary>A VM snapshot.</summary>
public class VmwareSnapshot
{
    public long Id { get; set; }
    public int SourceDeviceId { get; set; }
    public string VmMoRef { get; set; } = string.Empty;
    public string VmName { get; set; } = string.Empty;

    /// <summary>Snapshot MoRef (unique per vCenter).</summary>
    public string MoRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Size of the snapshot's delta disks and memory files, when vCenter reports it.</summary>
    public long? SizeBytes { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>A vCenter alarm that is currently triggered (red or yellow).</summary>
public class VmwareAlarm
{
    public long Id { get; set; }
    public int SourceDeviceId { get; set; }

    /// <summary>vCenter AlarmState key (unique while the alarm is triggered).</summary>
    public string Key { get; set; } = string.Empty;
    public string AlarmName { get; set; } = string.Empty;
    public string EntityMoRef { get; set; } = string.Empty;
    public string? EntityName { get; set; }

    /// <summary>red or yellow.</summary>
    public string Status { get; set; } = string.Empty;
    public bool Acknowledged { get; set; }
    public DateTime TriggeredUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>A vCenter/ESXi event (VM powered off, host disconnected, HA failover ...). Stored in events.db.</summary>
public class VmwareEvent
{
    public long Id { get; set; }
    public int SourceDeviceId { get; set; }

    /// <summary>vCenter event key (increasing).</summary>
    public long EventKey { get; set; }
    public DateTime TimestampUtc { get; set; }

    /// <summary>Event type, e.g. VmPoweredOffEvent.</summary>
    public string EventType { get; set; } = string.Empty;
    public Severity Severity { get; set; }
    public string? UserName { get; set; }
    public string? EntityName { get; set; }
    public string Message { get; set; } = string.Empty;
}
