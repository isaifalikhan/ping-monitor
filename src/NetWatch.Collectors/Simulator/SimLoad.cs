using System.Globalization;

namespace NetWatch.Collectors.Simulator;

/// <summary>
/// Load-test knobs of the simulator, from environment variables (all off by default):
/// <list type="bullet">
/// <item><c>NETWATCH_SIM_SCALE</c> (1–4): multiplies the distribution and access switches (2 = 240 switches, ~12,500 interfaces);</item>
/// <item><c>NETWATCH_SIM_DC_EVENTS_PER_MIN</c>: Security events per minute over both simulated DCs (default ≈ 120);</item>
/// <item><c>NETWATCH_SIM_SLOW_PCT</c>: share of devices (%) that answer slowly (2–2.8 s, below the default 3 s SNMP timeout);</item>
/// <item><c>NETWATCH_SIM_TIMEOUT_PCT</c>: chance (%) per poll that SNMP does not answer at all (the poll waits the full timeout).</item>
/// </list>
/// </summary>
public static class SimLoad
{
    public static int Scale { get; } = (int)Math.Clamp(Read("NETWATCH_SIM_SCALE", 1), 1, 4);

    public static double DcEventsPerMinute { get; } = Math.Clamp(Read("NETWATCH_SIM_DC_EVENTS_PER_MIN", 0), 0, 20_000);

    public static double SlowPercent { get; } = Math.Clamp(Read("NETWATCH_SIM_SLOW_PCT", 0), 0, 100);

    public static double TimeoutPercent { get; } = Math.Clamp(Read("NETWATCH_SIM_TIMEOUT_PCT", 0), 0, 100);

    public static bool Active => Scale > 1 || DcEventsPerMinute > 0 || SlowPercent > 0 || TimeoutPercent > 0;

    /// <summary>Slow devices are fixed per device so their effect on the poll cycle is stable.</summary>
    public static bool IsSlow(int deviceId) => SlowPercent > 0 && (uint)(deviceId * 2654435761u) % 10_000 < SlowPercent * 100;

    private static double Read(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
