namespace NetWatch.Core.Configuration;

/// <summary>The display/scheduling time zone: Asia/Karachi (Pakistan Standard Time, UTC+5, no DST).</summary>
public static class LocalTime
{
    public const string ZoneId = "Asia/Karachi";

    public static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { ZoneId, "Pakistan Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
            {
                return tz;
            }
        }

        // PKT has no daylight saving time, so a fixed offset is exact.
        return TimeZoneInfo.CreateCustomTimeZone(ZoneId, TimeSpan.FromHours(5), "Pakistan Standard Time", "PKT");
    }

    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
}
