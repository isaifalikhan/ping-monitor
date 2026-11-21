using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Core;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Core.Metrics;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Data.Metrics;

namespace NetWatch.Tests;

/// <summary>
/// Release QA (TEST_PLAN REL-20, REL-21): retention and roll-ups over simulated months. Data is written at
/// different ages, the clock is moved forward and the nightly retention run must remove exactly what is older than
/// the configured periods (raw 7 d, 5-min 90 d, hourly 730 d, events/audit/closed alerts 365 d), keep open alerts,
/// cascade alert history, purge expired sessions, and graphs over a long range must still show the rolled-up data.
/// </summary>
public sealed class QaRetentionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-qa-retention-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(new NetWatchPaths(_root));
        _sp = services.BuildServiceProvider();
        await _sp.InitialiseDatabasesAsync(NullLogger.Instance);
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Fact]
    public async Task Nightly_retention_removes_only_expired_data_after_months_of_operation()
    {
        var now = new DateTime(2028, 6, 15, 21, 30, 0, DateTimeKind.Utc); // 02:30 PKT
        var store = _sp.GetRequiredService<SqliteMetricStore>();
        var retention = _sp.GetRequiredService<SettingsService>().Retention;
        Assert.Equal((7, 90, 730, 365), (retention.RawDays, retention.FiveMinuteDays, retention.HourlyDays, retention.EventDays));

        // Metrics at four ages, each rolled up as the background roll-up service would do.
        var ages = new[] { 1, 30, 400, 1200 };
        foreach (var days in ages)
        {
            var t = now.AddDays(-days).Date.AddHours(10);
            for (var m = 0; m < 60; m++)
            {
                store.Enqueue(new DeviceSample(1, Ms(t.AddMinutes(m)), true, 2, 0, 30 + days % 50, 60, null));
            }

            store.Flush();
            store.RollupRawToFiveMinute(t, t.AddHours(1));
            store.RollupFiveMinuteToHourly(t, t.AddHours(1));
        }

        // Events, audit, alerts and sessions at the same ages.
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
            db.Devices.Add(new Device { Id = 1, Name = "SW-1", IpAddress = "10.0.0.1", CreatedUtc = now.AddYears(-3), StatusSinceUtc = now });
            var user = new User { Username = "qa", DisplayName = "qa", Role = UserRole.Viewer, CreatedUtc = now.AddYears(-3), UpdatedUtc = now };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            foreach (var days in ages)
            {
                var ts = now.AddDays(-days);
                events.DeviceEvents.Add(new DeviceEvent { TimestampUtc = ts, DeviceId = 1, Message = $"age {days}" });
                events.SyslogMessages.Add(new SyslogMessage { ReceivedUtc = ts, SourceIp = "10.0.0.1", DeviceId = 1, Message = $"age {days}" });
                events.DcSecurityEvents.Add(new DcSecurityEvent { TimestampUtc = ts, DeviceId = 1, EventId = 4625, RecordId = days, Category = "Logon" });
                db.AuditLog.Add(new AuditEntry { TimestampUtc = ts, Username = "qa", Action = $"Age{days}" });
                var closed = new Alert { DedupKey = $"closed-{days}", Title = $"closed {days}", State = AlertState.Closed, FirstSeenUtc = ts, LastSeenUtc = ts, ClosedUtc = ts, DeviceId = 1 };
                closed.History.Add(new AlertHistoryEntry { TimestampUtc = ts, Action = "Raised" });
                db.Alerts.Add(closed);
                db.Alerts.Add(new Alert { DedupKey = $"open-{days}", Title = $"open {days}", State = AlertState.Active, FirstSeenUtc = ts, LastSeenUtc = ts, DeviceId = 1 });
            }

            db.UserSessions.Add(new UserSession { Id = "expired", UserId = user.Id, CreatedUtc = now.AddDays(-2), LastSeenUtc = now.AddDays(-2), ExpiresUtc = now.AddDays(-1) });
            db.UserSessions.Add(new UserSession { Id = "revoked", UserId = user.Id, CreatedUtc = now.AddHours(-2), LastSeenUtc = now.AddHours(-1), ExpiresUtc = now.AddHours(10), RevokedUtc = now.AddHours(-1) });
            db.UserSessions.Add(new UserSession { Id = "live", UserId = user.Id, CreatedUtc = now.AddHours(-1), LastSeenUtc = now, ExpiresUtc = now.AddHours(11) });
            await db.SaveChangesAsync();
            await events.SaveChangesAsync();
        }

        var service = new RetentionService(_sp.GetRequiredService<IServiceScopeFactory>(), store, _sp.GetRequiredService<SettingsService>(),
            new FixedClock(now), NullLogger<RetentionService>.Instance);
        await service.RunOnceAsync(CancellationToken.None);

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
            Assert.Equal(["age 1", "age 30"], await events.DeviceEvents.OrderBy(e => e.TimestampUtc).Select(e => e.Message).ToArrayAsync().ContinueWith(t => t.Result.Reverse().ToArray()));
            Assert.Equal(2, await events.SyslogMessages.CountAsync());
            Assert.Equal(2, await events.DcSecurityEvents.CountAsync());
            Assert.Equal(["Age1", "Age30"], (await db.AuditLog.Select(a => a.Action).ToListAsync()).Order().ToArray());

            // Closed alerts older than 365 days are gone with their history; open alerts are never purged.
            var alerts = await db.Alerts.Select(a => a.DedupKey).ToListAsync();
            Assert.Equal(["closed-1", "closed-30", "open-1", "open-1200", "open-30", "open-400"], alerts.Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(2, await db.AlertHistory.CountAsync());
            Assert.Equal(["live"], await db.UserSessions.Select(s => s.Id).ToArrayAsync());
        }

        // Metrics: raw only for the last 7 days; graphs still show 30-day-old data (5-min) and 400-day-old data (hourly).
        var day1 = now.AddDays(-1).Date.AddHours(10);
        var raw = await store.QueryDeviceAsync(1, day1, day1.AddHours(1), CancellationToken.None);
        Assert.Equal(MetricResolution.Raw, raw.Resolution);
        Assert.Equal(60, raw.Points.Count);

        var day30 = now.AddDays(-30).Date.AddHours(10);
        var month = await store.QueryDeviceAsync(1, day30, day30.AddHours(1), CancellationToken.None);
        Assert.NotEmpty(month.Points);
        Assert.NotEqual(MetricResolution.Raw, month.Resolution);

        var day400 = now.AddDays(-400).Date.AddHours(10);
        var year = await store.QueryDeviceAsync(1, day400.AddDays(-1), day400.AddDays(1), CancellationToken.None);
        Assert.Single(year.Points);
        Assert.Equal(MetricResolution.Hourly, year.Resolution);

        // Hourly data is dropped in whole-year blocks (admin guide): 1,200 days is past the end of its year + 730 days.
        var day800 = now.AddDays(-1200).Date.AddHours(10);
        Assert.Empty((await store.QueryDeviceAsync(1, day800.AddDays(-1), day800.AddDays(1), CancellationToken.None)).Points);

        // A second run (next night) is idempotent.
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(60, (await store.QueryDeviceAsync(1, day1, day1.AddHours(1), CancellationToken.None)).Points.Count);
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }
}
