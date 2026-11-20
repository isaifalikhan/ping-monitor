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

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        [DeviceDown] = "Device down",
        [InterfaceDown] = "Interface down",
        [SensorProblem] = "Hardware sensor problem",
        [SnmpUnreachable] = "SNMP not responding",
        [ServiceDown] = "Watched service not running",
        [ServerCollectionFailed] = "Server monitoring (WinRM/WMI/SSH) failing",
        [PendingReboot] = "Server reboot pending",
    };
}

/// <summary>Well-known discrete event keys used by EventMatch, CountInWindow and Absence rules.</summary>
public static class EventKeys
{
    public const string DeviceReboot = "device.reboot";
    public const string SyslogMessage = "syslog.message";
    public const string SnmpTrap = "snmp.trap";
    public const string WindowsEvent = "windows.event";

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        [DeviceReboot] = "Device rebooted",
        [SyslogMessage] = "Syslog message",
        [SnmpTrap] = "SNMP trap",
        [WindowsEvent] = "Windows event log entry",
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
