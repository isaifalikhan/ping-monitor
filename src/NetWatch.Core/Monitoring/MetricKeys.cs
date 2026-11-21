namespace NetWatch.Core.Monitoring;

/// <summary>Well-known metric keys used by collectors and threshold rules.</summary>
public static class MetricKeys
{
    public const string PingRtt = "ping.rtt";
    public const string PingLoss = "ping.loss";
    public const string Cpu = "cpu.pct";
    public const string Memory = "mem.pct";
    public const string Temperature = "temp.c";

    /// <summary>Interface utilisation: the larger of in/out, in percent.</summary>
    public const string InterfaceUtilization = "iface.util";

    /// <summary>Interface errors per second (in + out).</summary>
    public const string InterfaceErrors = "iface.errors";

    /// <summary>Disk volume used space in percent (instance = volume).</summary>
    public const string DiskUsed = "disk.used.pct";

    /// <summary>Disk volume free space in GB (instance = volume).</summary>
    public const string DiskFreeGb = "disk.free.gb";

    /// <summary>Linux 5-minute load average divided by the number of CPUs.</summary>
    public const string LoadPerCpu = "load.per.cpu";

    /// <summary>Server network throughput, all adapters (bits/s).</summary>
    public const string NetInBps = "net.in.bps";
    public const string NetOutBps = "net.out.bps";

    /// <summary>Datastore used space in percent (instance = datastore).</summary>
    public const string DatastoreUsed = "vmware.datastore.used.pct";

    /// <summary>Snapshot age in days (instance = VM / snapshot).</summary>
    public const string SnapshotAgeDays = "vmware.snapshot.age.days";

    /// <summary>Snapshot size in GB (instance = VM / snapshot).</summary>
    public const string SnapshotSizeGb = "vmware.snapshot.size.gb";

    /// <summary>VM CPU ready in percent per vCPU.</summary>
    public const string VmCpuReady = "vmware.vm.cpuready.pct";

    /// <summary>VM ballooned memory in MB.</summary>
    public const string VmBalloonMb = "vmware.vm.balloon.mb";

    /// <summary>ESXi host CPU / memory usage in percent (instance = host).</summary>
    public const string HostCpu = "vmware.host.cpu.pct";
    public const string HostMemory = "vmware.host.mem.pct";

    /// <summary>Domain controller clock difference to the PDC emulator, in seconds (absolute).</summary>
    public const string DcTimeSkew = "dc.time.skew.sec";

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        [PingRtt] = "Ping latency (ms)",
        [PingLoss] = "Ping packet loss (%)",
        [Cpu] = "CPU usage (%)",
        [Memory] = "Memory usage (%)",
        [Temperature] = "Temperature (°C)",
        [InterfaceUtilization] = "Interface utilization (%)",
        [InterfaceErrors] = "Interface errors (per second)",
        [DiskUsed] = "Disk used (%)",
        [DiskFreeGb] = "Disk free (GB)",
        [LoadPerCpu] = "Load average per CPU (Linux)",
        [NetInBps] = "Server network in (bits/s)",
        [NetOutBps] = "Server network out (bits/s)",
        [DatastoreUsed] = "Datastore used (%)",
        [SnapshotAgeDays] = "Snapshot age (days)",
        [SnapshotSizeGb] = "Snapshot size (GB)",
        [VmCpuReady] = "VM CPU ready (%)",
        [VmBalloonMb] = "VM ballooned memory (MB)",
        [HostCpu] = "ESXi host CPU (%)",
        [HostMemory] = "ESXi host memory (%)",
        [DcTimeSkew] = "DC clock difference to PDC (s)",
    };
}

/// <summary>Well-known problem-state keys used by StateChange rules.</summary>
public static class StateKeys
{
    public const string DeviceDown = "device.down";
    public const string InterfaceDown = "iface.down";
    public const string SensorProblem = "sensor.problem";
    public const string SnmpUnreachable = "snmp.unreachable";
    public const string ServiceDown = "service.down";
    public const string ServerCollectionFailed = "server.unreachable";
    public const string PendingReboot = "server.pending.reboot";
    public const string VcenterUnreachable = "vmware.vcenter.unreachable";
    public const string HostNotConnected = "vmware.host.disconnected";
    public const string HostMaintenance = "vmware.host.maintenance";
    public const string HostHardware = "vmware.host.hardware";
    public const string DatastoreInaccessible = "vmware.datastore.inaccessible";
    public const string VmToolsNotRunning = "vmware.vm.tools";
    public const string VcenterAlarmRed = "vmware.alarm.red";
    public const string VcenterAlarmYellow = "vmware.alarm.yellow";
    public const string DcReplicationFailed = "dc.replication";
    public const string SelfCheck = "netwatch.selfcheck";

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        [DeviceDown] = "Device down",
        [InterfaceDown] = "Interface down",
        [SensorProblem] = "Hardware sensor problem",
        [SnmpUnreachable] = "SNMP not responding",
        [ServiceDown] = "Watched service not running",
        [ServerCollectionFailed] = "Server monitoring (WinRM/WMI/SSH) failing",
        [PendingReboot] = "Server reboot pending",
        [VcenterUnreachable] = "vCenter/ESXi API not reachable",
        [HostNotConnected] = "ESXi host not connected",
        [HostMaintenance] = "ESXi host in maintenance mode",
        [HostHardware] = "ESXi host hardware sensor alarm",
        [DatastoreInaccessible] = "Datastore not accessible",
        [VmToolsNotRunning] = "VMware Tools not running (powered-on VM)",
        [VcenterAlarmRed] = "vCenter alarm (red)",
        [VcenterAlarmYellow] = "vCenter alarm (yellow)",
        [DcReplicationFailed] = "AD replication failing (partner / naming context)",
        [SelfCheck] = "NetWatch self-check failing (disk, polling, storage, backups, certificate …)",
    };
}

/// <summary>Well-known discrete event keys used by EventMatch, CountInWindow and Absence rules.</summary>
public static class EventKeys
{
    public const string DeviceReboot = "device.reboot";
    public const string SyslogMessage = "syslog.message";
    public const string SnmpTrap = "snmp.trap";
    public const string WindowsEvent = "windows.event";
    public const string VmwareEvent = "vmware.event";
    public const string DcSecurity = "dc.security";

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        [DeviceReboot] = "Device rebooted",
        [SyslogMessage] = "Syslog message",
        [SnmpTrap] = "SNMP trap",
        [WindowsEvent] = "Windows event log entry",
        [VmwareEvent] = "vCenter event",
        [DcSecurity] = "DC security event (Security log)",
    };
}

/// <summary>Severity conversions for event sources.</summary>
public static class SeverityMapping
{
    /// <summary>Syslog 0–2 (emergency, alert, critical) → Critical, 3 error → Major, 4 warning → Warning, else Info.</summary>
    public static Severity FromSyslog(int syslogSeverity) => syslogSeverity switch
    {
        <= 2 => Severity.Critical,
        3 => Severity.Major,
        4 => Severity.Warning,
        _ => Severity.Info,
    };

    /// <summary>Windows event level 1 Critical → Critical, 2 Error → Major, 3 Warning → Warning, else Info.</summary>
    public static Severity FromWindowsLevel(int level) => level switch
    {
        1 => Severity.Critical,
        2 => Severity.Major,
        3 => Severity.Warning,
        _ => Severity.Info,
    };

    public static string SyslogSeverityName(int severity) => severity switch
    {
        0 => "Emergency",
        1 => "Alert",
        2 => "Critical",
        3 => "Error",
        4 => "Warning",
        5 => "Notice",
        6 => "Informational",
        7 => "Debug",
        _ => severity.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
