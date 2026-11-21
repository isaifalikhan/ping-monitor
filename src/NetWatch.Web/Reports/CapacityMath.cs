namespace NetWatch.Web.Reports;

/// <summary>Capacity estimates used by the reports (pure, unit tested).</summary>
public static class CapacityMath
{
    /// <summary>
    /// Days until a volume is full when the growth seen in the period (percentage points over <paramref name="periodDays"/>)
    /// continues linearly. Null when it is not growing (or the growth is too small to measure).
    /// </summary>
    public static double? DaysUntilFull(double usedPct, double? growthPoints, double periodDays)
    {
        if (growthPoints is not { } g || g <= 0.01 || periodDays <= 0)
        {
            return null;
        }

        return usedPct >= 100 ? 0 : Math.Max(0, (100 - usedPct) / (g / periodDays));
    }

    /// <summary>Average of the second half of the values minus the first half; null for fewer than 4 values.</summary>
    public static double? Trend(IReadOnlyList<double> values)
    {
        if (values.Count < 4)
        {
            return null;
        }

        var half = values.Count / 2;
        return values.Skip(values.Count - half).Average() - values.Take(half).Average();
    }
}
