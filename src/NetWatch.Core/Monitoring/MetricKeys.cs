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

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        [PingRtt] = "Ping latency (ms)",
        [PingLoss] = "Ping packet loss (%)",
        [Cpu] = "CPU usage (%)",
        [Memory] = "Memory usage (%)",
        [Temperature] = "Temperature (°C)",
        [InterfaceUtilization] = "Interface utilization (%)",
        [InterfaceErrors] = "Interface errors (per second)",
    };
}

/// <summary>Well-known problem-state keys used by StateChange rules.</summary>
public static class StateKeys
{
    public const string DeviceDown = "device.down";
    public const string InterfaceDown = "iface.down";
    public const string SensorProblem = "sensor.problem";
    public const string SnmpUnreachable = "snmp.unreachable";

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        [DeviceDown] = "Device down",
        [InterfaceDown] = "Interface down",
        [SensorProblem] = "Hardware sensor problem",
        [SnmpUnreachable] = "SNMP not responding",
    };
}

/// <summary>Well-known discrete event keys used by EventMatch rules.</summary>
public static class EventKeys
{
    public const string DeviceReboot = "device.reboot";

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        [DeviceReboot] = "Device rebooted",
    };
}
