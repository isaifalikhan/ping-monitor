using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Alerts;
using NetWatch.Collectors.DomainControllers;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Processing;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Data.Metrics;
using NetWatch.Web.Api;

namespace NetWatch.Tests;

/// <summary>
/// Retention, date-range search and CSV export of stored events and alerts (DC security events, syslog, traps,
/// Windows event logs, alert history), and the optional backfill of a DC's Security log on first contact.
/// Rows are written 10, 45, 100, 200 and 400 days back; searches over 30 / 90 / 365 days must find exactly the
/// rows inside the range, and the retention run (365 days) must remove only the 400-day rows.
/// </summary>
public sealed class RetentionSearchExportTests : IAsyncLifetime
{
    private static readonly int[] Ages = [10, 45, 100, 200, 400];
    private static readonly DateTime Now = new(2028, 6, 15, 21, 30, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-retention-search-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(new NetWatchPaths(_root));
        _sp = services.BuildServiceProvider();
        await _sp.InitialiseDatabasesAsync(NullLogger.Instance);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        db.Devices.Add(new Device { Id = 1, Name = "DC01", IpAddress = "10.0.0.11", Type = DeviceType.DomainController, CreatedUtc = Now.AddYears(-3), StatusSinceUtc = Now });
        foreach (var days in Ages)
        {
            var ts = Now.AddDays(-days);
            var tag = $"age-{days}";
            events.DcSecurityEvents.Add(new DcSecurityEvent { TimestampUtc = ts, DeviceId = 1, EventId = 4625, RecordId = days, Category = "LogonFailure", TargetUser = tag, Message = $"Failed logon {tag}" });
            events.SyslogMessages.Add(new SyslogMessage { ReceivedUtc = ts, SourceIp = "10.0.0.1", DeviceId = 1, SyslogSeverity = 3, Message = $"link down {tag}" });
            events.SnmpTraps.Add(new SnmpTrap { ReceivedUtc = ts, SourceIp = "10.0.0.1", DeviceId = 1, Version = "v2c", TrapOid = "1.3.6.1.6.3.1.1.5.3", Summary = $"linkDown {tag}" });
            events.WindowsEvents.Add(new WindowsEventEntry { TimestampUtc = ts, DeviceId = 1, LogName = "System", Provider = "Service Control Manager", EventId = 7031, Level = 2, RecordId = days, Message = $"Spooler stopped {tag}" });
            events.VmwareEvents.Add(new VmwareEvent { SourceDeviceId = 1, EventKey = days, TimestampUtc = ts, EventType = "VmPoweredOffEvent", Message = $"VM off {tag}" });
            db.Alerts.Add(new Alert { DedupKey = $"closed-{days}", Title = $"Service not running {tag}", Message = "m", State = AlertState.Closed, DeviceId = 1, FirstSeenUtc = ts, LastSeenUtc = ts, ClosedUtc = ts });
        }

        await db.SaveChangesAsync();
        await events.SaveChangesAsync();
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

    private static int AgeOf(string text) => int.Parse(text[(text.IndexOf("age-", StringComparison.Ordinal) + 4)..], System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(30, new[] { 10 })]
    [InlineData(90, new[] { 10, 45 })]
    [InlineData(365, new[] { 10, 45, 100, 200 })]
    public async Task Every_event_store_and_the_alert_history_can_be_searched_by_date_range(int daysBack, int[] expected)
    {
        var from = Now.AddDays(-daysBack);
        using var scope = _sp.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();

        int[] Ages(IEnumerable<string> texts) => texts.Select(AgeOf).Order().ToArray();

        Assert.Equal(expected, Ages(await DcEndpoints.Filter(events, null, null, null, null, null, null, "age-", null, null, from, null).Select(e => e.Message).ToListAsync()));
        Assert.Equal(expected, Ages(await EventEndpoints.FilterSyslog(events, null, null, "age-", null, from, null).Select(e => e.Message).ToListAsync()));
        Assert.Equal(expected, Ages(await EventEndpoints.FilterTraps(events, null, null, "age-", from, null).Select(e => e.Summary).ToListAsync()));
        Assert.Equal(expected, Ages(await EventEndpoints.FilterWindowsEvents(events, null, null, null, "age-", from, null).Select(e => e.Message).ToListAsync()));

        // Alert history goes through the same service as the Alerts page (list) and its CSV export.
        var alerts = new AlertService(db, null!, new NoLive());
        var query = new AlertQuery([AlertState.Closed], null, null, "age-", from, null, 1, 1000);
        Assert.Equal(expected, Ages((await alerts.ListAsync(query, CancellationToken.None)).Items.Select(a => a.Title)));
        Assert.Equal(expected, Ages((await alerts.ExportAsync(query, 100_000, CancellationToken.None)).Select(a => a.Title)));
    }

    [Fact]
    public async Task A_closed_range_returns_only_rows_inside_it()
    {
        using var scope = _sp.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();

        // 90 to 30 days back: only the 45-day row.
        var rows = await EventEndpoints.FilterSyslog(events, null, null, null, null, Now.AddDays(-90), Now.AddDays(-30)).ToListAsync();
        Assert.Equal([45], rows.Select(r => AgeOf(r.Message)).ToArray());
    }

    [Fact]
    public async Task Retention_removes_events_and_closed_alerts_older_than_365_days_in_every_store()
    {
        var service = new RetentionService(_sp.GetRequiredService<IServiceScopeFactory>(), _sp.GetRequiredService<SqliteMetricStore>(),
            _sp.GetRequiredService<SettingsService>(), new FixedClock(Now), NullLogger<RetentionService>.Instance);
        await service.RunOnceAsync(CancellationToken.None);

        using var scope = _sp.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        int[] kept = [10, 45, 100, 200];
        Assert.Equal(kept, (await events.DcSecurityEvents.Select(e => e.Message).ToListAsync()).Select(AgeOf).Order().ToArray());
        Assert.Equal(kept, (await events.SyslogMessages.Select(e => e.Message).ToListAsync()).Select(AgeOf).Order().ToArray());
        Assert.Equal(kept, (await events.SnmpTraps.Select(e => e.Summary).ToListAsync()).Select(AgeOf).Order().ToArray());
        Assert.Equal(kept, (await events.WindowsEvents.Select(e => e.Message).ToListAsync()).Select(AgeOf).Order().ToArray());
        Assert.Equal(kept, (await events.VmwareEvents.Select(e => e.Message).ToListAsync()).Select(AgeOf).Order().ToArray());
        Assert.Equal(kept, (await db.Alerts.Select(a => a.Title).ToListAsync()).Select(AgeOf).Order().ToArray());
    }

    [Fact]
    public void Csv_export_has_a_bom_a_header_and_neutralises_formulas()
    {
        var bytes = CsvExport.Write(["Time (PKT)", "Message"], [["2028-06-15 10:00:00", "=HYPERLINK(\"x\")"], ["2028-06-15 10:01:00", "plain, with comma"]]);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Time (PKT),Message", lines[0]);
        Assert.StartsWith("2028-06-15 10:00:00,\"'=HYPERLINK", lines[1], StringComparison.Ordinal);
        Assert.Equal("2028-06-15 10:01:00,\"plain, with comma\"", lines[2]);
        Assert.Equal("2028-06-15 15:30:00", CsvExport.Time(new DateTime(2028, 6, 15, 10, 30, 0, DateTimeKind.Utc)));
    }

    // ------------------------------------------------------------------ DC backfill on first contact

    [Fact]
    public void Backfill_is_on_by_default_for_24_hours_and_bounded()
    {
        Assert.Equal(24, new DcSettings().InitialBackfillHours);
        var s = new DcSettings { InitialBackfillHours = 10_000 };
        s.Normalise();
        Assert.Equal(720, s.InitialBackfillHours);
        s.InitialBackfillHours = -5;
        s.Normalise();
        Assert.Equal(0, s.InitialBackfillHours);
    }

    [Fact]
    public void First_contact_starts_before_the_oldest_event_of_the_window_and_records_the_backfill_end()
    {
        // History in the window: start just before record 900, backfill ends at the newest record (1,000).
        Assert.Equal([(DcLogs.Security, 899L), (DcLogs.SecurityBackfillEnd, 1000L)], DcCollector.FirstContactBookmarks(1000, 900));

        // No backfill (0 h or nothing in the window): only new events, as before.
        Assert.Equal([(DcLogs.Security, 1000L)], DcCollector.FirstContactBookmarks(1000, null));

        // Empty log.
        Assert.Equal([(DcLogs.Security, 0L)], DcCollector.FirstContactBookmarks(0, null));

        var xpaths = DcCollector.BackfillXPaths(24);
        Assert.All(xpaths, x => Assert.Contains("TimeCreated[timediff(@SystemTime) <= 86400000]", x, StringComparison.Ordinal));
        Assert.Contains(xpaths, x => x.Contains("EventID=4625", StringComparison.Ordinal));
        Assert.Contains(xpaths, x => x.Contains("EventID=1102", StringComparison.Ordinal));
    }

    [Fact]
    public void Security_queries_stay_under_the_event_log_xpath_limit_and_cover_every_catalogued_id()
    {
        // Windows rejects an XPath with more than about 20 EventID conditions ("The specified query is invalid").
        var xpaths = DcCollector.SecurityXPaths("EventRecordID > 5");
        Assert.All(xpaths, x => Assert.InRange(System.Text.RegularExpressions.Regex.Count(x, "EventID="), 1, 10));
        var ids = xpaths.SelectMany(x => System.Text.RegularExpressions.Regex.Matches(x, @"EventID=(\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .Order().ToList();
        Assert.Equal(DcEventCatalog.Events.Keys.Order().ToList(), ids);
        Assert.All(xpaths, x => Assert.EndsWith("and EventRecordID > 5]]", x, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backfilled_events_are_stored_but_raise_no_alerts()
    {
        var sink = new CapturingSink();
        var processor = new DcResultProcessor(_sp.GetRequiredService<IServiceScopeFactory>(), _sp.GetRequiredService<IMetricStore>(), sink,
            _sp.GetRequiredService<SettingsService>(), null!, TimeProvider.System, NullLogger<DcResultProcessor>.Instance);
        var tags = new HashSet<string>();
        var device = new DeviceSnapshot(1, "DC01", "10.0.0.11", DeviceType.DomainController, 60, true, false, null, null, tags, new HashSet<int>(),
            new DeviceContext(1, "DC01", "10.0.0.11", DeviceType.DomainController, new HashSet<int>(), tags, false));

        // Records 5001–5010 were in the log before monitoring started (backfill end = 5010); 5011–5015 are new.
        var readings = Enumerable.Range(5001, 15)
            .Select(i => new DcSecurityReading(i, DateTime.UtcNow.AddMinutes(-30 + i - 5001), 4740, "DC01", "SYSTEM", "CORP", $"user{i}", "WS-1", null, null, null, "WS-1", null, null, null, false))
            .ToList();
        using var scope = _sp.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        var stored = await processor.IngestSecurityAsync(device, readings, events, CancellationToken.None, backfillEndRecordId: 5010);

        Assert.Equal(15, stored);
        var signalled = sink.Signals.OfType<EventSignal>().Select(s => s.Fields!["targetUser"]).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Enumerable.Range(5011, 5).Select(i => $"user{i}").ToArray(), signalled);
    }

    private sealed class CapturingSink : IAlertSignalSink
    {
        public List<AlertSignal> Signals { get; } = new();

        public void Publish(AlertSignal signal)
        {
            lock (Signals)
            {
                Signals.Add(signal);
            }
        }
    }

    private sealed class NoLive : ILiveUpdatePublisher
    {
        public Task PublishAsync(string eventName, object payload, string? group = null) => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }
}
