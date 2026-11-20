using NetWatch.Core.Metrics;

namespace NetWatch.Tests;

public class CounterMathTests
{
    private static CounterSnapshot Snap(long tsSec, ulong inOctets, ulong outOctets, bool hc = true, ulong errors = 0) =>
        new(tsSec * 1000, inOctets, outOctets, errors, 0, 0, 0, hc);

    [Fact]
    public void Delta_Normal() => Assert.Equal(50UL, CounterMath.Delta(100, 150, true));

    [Fact]
    public void Delta_Counter32Wrap() => Assert.Equal(16UL, CounterMath.Delta(uint.MaxValue - 5, 10, is64Bit: false));

    [Fact]
    public void Delta_Counter64DecreaseIsReset() => Assert.Null(CounterMath.Delta(1_000_000, 10, is64Bit: true));

    [Fact]
    public void Rates_ComputesBpsAndUtilisation()
    {
        // 75,000,000 bytes in 60 s = 10 Mbit/s on a 100 Mbit/s port = 10 %.
        var rates = CounterMath.ComputeRates(Snap(0, 0, 0), Snap(60, 75_000_000, 37_500_000), 100_000_000, false);
        Assert.NotNull(rates);
        Assert.Equal(10_000_000, rates!.Value.InBps!.Value, 3);
        Assert.Equal(5_000_000, rates.Value.OutBps!.Value, 3);
        Assert.Equal(10, rates.Value.InUtilPct!.Value, 6);
        Assert.Equal(5, rates.Value.OutUtilPct!.Value, 6);
    }

    [Fact]
    public void Rates_NullAfterReboot() =>
        Assert.Null(CounterMath.ComputeRates(Snap(0, 0, 0), Snap(60, 1000, 1000), 1_000_000_000, deviceRebooted: true));

    [Fact]
    public void Rates_NullWhenGapTooLong() =>
        Assert.Null(CounterMath.ComputeRates(Snap(0, 0, 0), Snap(3600, 1000, 1000), 1_000_000_000, false));

    [Fact]
    public void Rates_ImpossibleSpikeIsDiscarded()
    {
        // 1 GB in 60 s on a 10 Mbit/s port is impossible.
        var rates = CounterMath.ComputeRates(Snap(0, 0, 0), Snap(60, 1_000_000_000, 0), 10_000_000, false);
        Assert.Null(rates!.Value.InBps);
        Assert.Equal(0, rates.Value.OutBps);
    }

    [Fact]
    public void Rates_UnknownSpeedGivesNoUtilisation()
    {
        var rates = CounterMath.ComputeRates(Snap(0, 0, 0), Snap(60, 750, 0), 0, false);
        Assert.Equal(100, rates!.Value.InBps!.Value, 6);
        Assert.Null(rates.Value.InUtilPct);
    }

    [Fact]
    public void Rates_Counter32OctetsWrap()
    {
        var rates = CounterMath.ComputeRates(Snap(0, uint.MaxValue - 999, 0, hc: false), Snap(10, 1000, 0, hc: false), 1_000_000_000, false);
        Assert.Equal(2000 * 8 / 10.0, rates!.Value.InBps!.Value, 6);
    }

    [Fact]
    public void Rates_ErrorsPerSecond()
    {
        var rates = CounterMath.ComputeRates(Snap(0, 0, 0, errors: 10), Snap(20, 0, 0, errors: 30), 1_000_000_000, false);
        Assert.Equal(1.0, rates!.Value.InErrPs!.Value, 6);
    }
}
