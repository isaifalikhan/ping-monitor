namespace NetWatch.Core.Metrics;

/// <summary>Derived per-second rates for one interface between two counter snapshots.</summary>
public readonly record struct InterfaceRates(
    double? InBps,
    double? OutBps,
    double? InUtilPct,
    double? OutUtilPct,
    double? InErrPs,
    double? OutErrPs,
    double? InDiscPs,
    double? OutDiscPs);

/// <summary>
/// Pure helpers converting SNMP counters into rates. Handles 32-bit wrap, counter resets (device reboot or
/// interface reset) and implausible spikes.
/// </summary>
public static class CounterMath
{
    /// <summary>Samples further apart than this are not used for a rate (data would be misleading).</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Difference between two counter readings. Returns null if the counter appears to have been reset.
    /// </summary>
    /// <param name="previous">Earlier value.</param>
    /// <param name="current">Later value.</param>
    /// <param name="is64Bit">True for Counter64 (ifHC*), false for Counter32.</param>
    public static ulong? Delta(ulong previous, ulong current, bool is64Bit)
    {
        if (current >= previous)
        {
            return current - previous;
        }

        // A Counter32 that wrapped past 2^32. 64-bit counters never wrap in practice, so a decrease means a reset.
        if (!is64Bit && previous <= uint.MaxValue && current <= uint.MaxValue)
        {
            return (uint.MaxValue - previous) + current + 1;
        }

        return null;
    }

    /// <summary>
    /// Computes interface rates between two snapshots.
    /// </summary>
    /// <param name="previous">Earlier snapshot.</param>
    /// <param name="current">Later snapshot.</param>
    /// <param name="speedBps">Interface speed in bit/s (0 = unknown; utilisation is then null).</param>
    /// <param name="deviceRebooted">If true, counters were reset and no rate can be derived.</param>
    /// <returns>Null when no rate can be derived (first sample, reboot, gap too long).</returns>
    public static InterfaceRates? ComputeRates(CounterSnapshot previous, CounterSnapshot current, long speedBps, bool deviceRebooted)
    {
        if (deviceRebooted)
        {
            return null;
        }

        var seconds = (current.TsMs - previous.TsMs) / 1000.0;
        if (seconds <= 0.5 || seconds > MaxGap.TotalSeconds)
        {
            return null;
        }

        // Error/discard counters are always Counter32 in IF-MIB; octets depend on HC support.
        var inBps = Bits(previous.InOctets, current.InOctets, current.HighCapacity, seconds, speedBps);
        var outBps = Bits(previous.OutOctets, current.OutOctets, current.HighCapacity, seconds, speedBps);

        return new InterfaceRates(
            inBps,
            outBps,
            Utilisation(inBps, speedBps),
            Utilisation(outBps, speedBps),
            PerSecond(previous.InErrors, current.InErrors, seconds),
            PerSecond(previous.OutErrors, current.OutErrors, seconds),
            PerSecond(previous.InDiscards, current.InDiscards, seconds),
            PerSecond(previous.OutDiscards, current.OutDiscards, seconds));
    }

    private static double? Bits(ulong? prev, ulong? curr, bool hc, double seconds, long speedBps)
    {
        if (prev is null || curr is null)
        {
            return null;
        }

        var delta = Delta(prev.Value, curr.Value, hc);
        if (delta is null)
        {
            return null;
        }

        var bps = delta.Value * 8.0 / seconds;

        // Discard physically impossible spikes (e.g. counter reset that looked like a wrap).
        if (speedBps > 0 && bps > speedBps * 1.5)
        {
            return null;
        }

        return bps;
    }

    private static double? PerSecond(ulong? prev, ulong? curr, double seconds)
    {
        if (prev is null || curr is null)
        {
            return null;
        }

        var delta = Delta(prev.Value, curr.Value, is64Bit: false);
        return delta is null ? null : delta.Value / seconds;
    }

    private static double? Utilisation(double? bps, long speedBps)
    {
        if (bps is null || speedBps <= 0)
        {
            return null;
        }

        return Math.Min(100.0, bps.Value / speedBps * 100.0);
    }
}
