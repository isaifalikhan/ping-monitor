using System.Globalization;
using Cronos;
using NetWatch.Core;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;

namespace NetWatch.Data.Config;

/// <summary>
/// Evaluates maintenance windows. Daily and weekly windows are converted to cron expressions in local time
/// (Asia/Karachi), so all recurring windows share one code path: a window is active at time t when a recurrence
/// started within the last DurationMin minutes.
/// </summary>
public static class MaintenanceSchedule
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>Validates a window. Returns an error message or null.</summary>
    public static string? Validate(MaintenanceWindow w)
    {
        if (string.IsNullOrWhiteSpace(w.Name) || w.Name.Length > 200)
        {
            return "Name is required (max 200 characters).";
        }

        switch (w.Recurrence)
        {
            case MaintenanceRecurrence.Once:
                if (w.StartUtc is null || w.EndUtc is null)
                {
                    return "Enter a start and an end time.";
                }

                if (w.EndUtc <= w.StartUtc)
                {
                    return "The end must be after the start.";
                }

                if (w.EndUtc - w.StartUtc > TimeSpan.FromDays(366))
                {
                    return "A one-off window can last at most one year.";
                }

                return null;

            case MaintenanceRecurrence.Daily:
            case MaintenanceRecurrence.Weekly:
                if (!TryParseTime(w.StartTimeLocal, out _))
                {
                    return "Enter the start time as HH:mm.";
                }

                if (w.Recurrence == MaintenanceRecurrence.Weekly && ParseDays(w.DaysOfWeek).Count == 0)
                {
                    return "Select at least one weekday.";
                }

                return ValidateDuration(w);

            case MaintenanceRecurrence.Cron:
                if (TryParseCron(w.CronExpression) is null)
                {
                    return "The cron expression is invalid (5 fields: minute hour day month weekday).";
                }

                return ValidateDuration(w);

            default:
                return "Unknown recurrence.";
        }
    }

    private static string? ValidateDuration(MaintenanceWindow w) =>
        w.DurationMin is < 1 or > 7 * 24 * 60 ? "Duration must be between 1 minute and 7 days." : null;

    /// <summary>True when the window is enabled and active at <paramref name="nowUtc"/>.</summary>
    public static bool IsActive(MaintenanceWindow w, DateTime nowUtc) => w.IsEnabled && CurrentEnd(w, nowUtc) is not null;

    /// <summary>End of the occurrence that is active at <paramref name="nowUtc"/>, or null if the window is not active.</summary>
    public static DateTime? CurrentEnd(MaintenanceWindow w, DateTime nowUtc)
    {
        if (w.Recurrence == MaintenanceRecurrence.Once)
        {
            return w.StartUtc <= nowUtc && nowUtc < w.EndUtc ? w.EndUtc : null;
        }

        // Recurring: StartUtc/EndUtc optionally bound the dates on which the schedule applies.
        if ((w.StartUtc is { } from && nowUtc < from) || (w.EndUtc is { } until && nowUtc >= until))
        {
            return null;
        }

        var cron = ToCron(w);
        if (cron is null || w.DurationMin <= 0)
        {
            return null;
        }

        var duration = TimeSpan.FromMinutes(w.DurationMin);
        var occurrences = cron.GetOccurrences(
            DateTime.SpecifyKind(nowUtc - duration, DateTimeKind.Utc),
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
            LocalTime.Zone,
            fromInclusive: false,
            toInclusive: true);

        DateTime? end = null;
        foreach (var start in occurrences)
        {
            var e = start + duration;
            if (e > nowUtc && (end is null || e > end))
            {
                end = e;
            }
        }

        return end;
    }

    /// <summary>Next start of the window after <paramref name="nowUtc"/> (for display), or null.</summary>
    public static DateTime? NextStart(MaintenanceWindow w, DateTime nowUtc)
    {
        if (!w.IsEnabled)
        {
            return null;
        }

        if (w.Recurrence == MaintenanceRecurrence.Once)
        {
            return w.StartUtc > nowUtc ? w.StartUtc : null;
        }

        var cron = ToCron(w);
        var from = w.StartUtc is { } s && s > nowUtc ? s : nowUtc;
        var next = cron?.GetNextOccurrence(DateTime.SpecifyKind(from, DateTimeKind.Utc), LocalTime.Zone);
        return next is { } n && (w.EndUtc is null || n < w.EndUtc) ? n : null;
    }

    /// <summary>Human-readable schedule, e.g. "Weekly Sun, Wed at 02:00 for 120 min".</summary>
    public static string Describe(MaintenanceWindow w) => w.Recurrence switch
    {
        MaintenanceRecurrence.Once => "One-off",
        MaintenanceRecurrence.Daily => $"Daily at {w.StartTimeLocal} PKT for {w.DurationMin} min",
        MaintenanceRecurrence.Weekly => $"Weekly {string.Join(", ", ParseDays(w.DaysOfWeek).Select(d => DayNames[(int)d]))} at {w.StartTimeLocal} PKT for {w.DurationMin} min",
        _ => $"Cron \"{w.CronExpression}\" (PKT) for {w.DurationMin} min",
    };

    internal static CronExpression? ToCron(MaintenanceWindow w)
    {
        switch (w.Recurrence)
        {
            case MaintenanceRecurrence.Daily when TryParseTime(w.StartTimeLocal, out var t):
                return CronExpression.Parse(string.Create(CultureInfo.InvariantCulture, $"{t.Minutes} {t.Hours} * * *"));
            case MaintenanceRecurrence.Weekly when TryParseTime(w.StartTimeLocal, out var t):
                var days = ParseDays(w.DaysOfWeek);
                return days.Count == 0
                    ? null
                    : CronExpression.Parse(string.Create(CultureInfo.InvariantCulture, $"{t.Minutes} {t.Hours} * * {string.Join(",", days.Select(d => (int)d))}"));
            case MaintenanceRecurrence.Cron:
                return TryParseCron(w.CronExpression);
            default:
                return null;
        }
    }

    private static CronExpression? TryParseCron(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        try
        {
            return CronExpression.Parse(expression.Trim());
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    internal static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);

    internal static List<DayOfWeek> ParseDays(string? text) =>
        (text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => Array.FindIndex(DayNames, n => n.Equals(d[..Math.Min(3, d.Length)], StringComparison.OrdinalIgnoreCase)))
            .Where(i => i >= 0)
            .Distinct()
            .OrderBy(i => i)
            .Select(i => (DayOfWeek)i)
            .ToList();
}
