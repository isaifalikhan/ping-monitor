namespace NetWatch.Collectors.Processing;

/// <summary>
/// Counts link state changes of one port. A change of ifOperStatus between two polls counts as one; a moved
/// ifLastChange while the status is the same as before counts as two (the link went down and came back between
/// polls, which polling alone would miss). Pure and unit tested.
/// </summary>
public sealed class FlapTracker
{
    private readonly List<(DateTime At, int Changes)> _changes = new();
    private int _lastOper;
    private long? _lastChangeTicks;

    /// <summary>Changes within the flap window after the last observation.</summary>
    public int InWindow { get; private set; }

    /// <summary>Changes within the last 24 hours.</summary>
    public int Last24h { get; private set; }

    /// <param name="lastChangeTicks">ifLastChange (sysUpTime ticks at the last change), or null when not supported.</param>
    /// <param name="rebooted">The device rebooted since the previous poll (ifLastChange restarts from zero).</param>
    public void Observe(DateTime now, int oper, long? lastChangeTicks, bool rebooted, TimeSpan window)
    {
        var changes = 0;
        if (_lastOper != 0 && !rebooted)
        {
            if (oper != _lastOper)
            {
                changes = 1;
            }
            else if (lastChangeTicks is { } t && _lastChangeTicks is { } previous && t != previous)
            {
                changes = 2;
            }
        }

        _lastOper = oper;
        _lastChangeTicks = lastChangeTicks;
        if (changes > 0)
        {
            _changes.Add((now, changes));
        }

        _changes.RemoveAll(c => now - c.At > TimeSpan.FromHours(24));
        Last24h = _changes.Sum(c => c.Changes);
        InWindow = _changes.Where(c => now - c.At <= window).Sum(c => c.Changes);
    }
}

/// <summary>Port health checks for important ports (pure).</summary>
public static class PortChecks
{
    /// <summary>EtherLike-MIB dot3StatsDuplexStatus: 1 unknown, 2 halfDuplex, 3 fullDuplex.</summary>
    public const int HalfDuplex = 2;

    public static bool IsHalfDuplex(int operStatus, int? duplex) => operStatus == 1 && duplex == HalfDuplex;

    /// <summary>An up port that negotiated below the expected speed (e.g. a 10G uplink running at 1G).</summary>
    public static bool IsBelowExpectedSpeed(int operStatus, long speedBps, long? expectedSpeedBps) =>
        operStatus == 1 && expectedSpeedBps is > 0 && speedBps > 0 && speedBps < expectedSpeedBps;

    public static string DuplexText(int? duplex) => duplex switch
    {
        2 => "half",
        3 => "full",
        _ => "unknown",
    };

    public static string SpeedText(long bps) => bps switch
    {
        >= 1_000_000_000 when bps % 1_000_000_000 == 0 => $"{bps / 1_000_000_000} Gbit/s",
        >= 1_000_000 => $"{bps / 1_000_000} Mbit/s",
        _ => $"{bps} bit/s",
    };
}
