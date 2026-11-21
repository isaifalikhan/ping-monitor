namespace NetWatch.Core.Entities;

/// <summary>A GPU of a server (Win32_VideoController / lspci, GPU performance counters, nvidia-smi). Latest values.</summary>
public class ServerGpu
{
    public int Id { get; set; }
    public int DeviceId { get; set; }

    /// <summary>Position of the GPU on the server (nvidia-smi index, or adapter order on Windows).</summary>
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Vendor { get; set; }
    public string? DriverVersion { get; set; }
    public double? UtilizationPct { get; set; }
    public double? MemoryUsedMb { get; set; }
    public double? MemoryTotalMb { get; set; }
    public double? TemperatureC { get; set; }
    public double? PowerW { get; set; }
    public double? FanPct { get; set; }

    /// <summary>Driver error or device status text; null when the GPU is healthy.</summary>
    public string? Problem { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One hardware component reported by the server's management controller (Redfish or SNMP): the system itself,
/// processors, memory, temperatures, fans, power supplies, storage controllers and drives. Replaced on every read.
/// </summary>
public class HardwareComponent
{
    public int Id { get; set; }
    public int DeviceId { get; set; }

    /// <summary>"Redfish" or "SNMP".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>System, Processor, Memory, Temperature, Fan, PowerSupply, StorageController, Drive, Volume.</summary>
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Redfish Status.Health: OK, Warning, Critical (null = not reported).</summary>
    public string? Health { get; set; }

    /// <summary>Redfish Status.State: Enabled, Absent, Disabled, StandbyOffline ...</summary>
    public string? State { get; set; }
    public double? Reading { get; set; }
    public string? Unit { get; set; }

    /// <summary>Model, serial, firmware, capacity ... (short free text).</summary>
    public string? Details { get; set; }
    public DateTime LastSeenUtc { get; set; }
}
