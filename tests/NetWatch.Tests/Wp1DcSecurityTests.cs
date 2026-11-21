using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Collectors.DomainControllers;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Processing;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Core.Monitoring;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Data.Events;

namespace NetWatch.Tests;

/// <summary>
/// WP1: every collected Security event ID parsed from a full sample event (Samples/Dc/&lt;id&gt;.xml, the XML that
/// EventRecord.ToXml() returns), status decoding, the 4624 storage filter, per-DC forwarding, duplicate-free
/// re-ingestion after a restart, and ingestion throughput.
/// </summary>
public sealed class Wp1DcSecurityTests
{
    private static string Sample(int id) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "Dc", $"{id}.xml"));

    public static TheoryData<int, string?, string?, string?, string?, bool> Expectations => new()
    {
        // id,  target user,          subject user, source IP,      group,               failed
        { 4624, "ayesha.khan",        "DC01$",      "10.10.20.45",  null,                false },
        { 4625, "bilal.ahmed",        null,         "10.10.31.77",  null,                true },
        { 4740, "bilal.ahmed",        "DC01$",      null,           null,                false },
        { 4767, "bilal.ahmed",        "adm.saif",   null,           null,                false },
        { 4720, "temp.contractor",    "adm.saif",   null,           null,                false },
        { 4722, "temp.contractor",    "adm.saif",   null,           null,                false },
        { 4725, "temp.contractor",    "adm.saif",   null,           null,                false },
        { 4726, "temp.contractor",    "adm.saif",   null,           null,                false },
        { 4738, "ayesha.khan",        "adm.saif",   null,           null,                false },
        { 4723, "ayesha.khan",        "ayesha.khan", null,          null,                false },
        { 4724, "bilal.ahmed",        "adm.saif",   null,           null,                false },
        { 4728, "Usman Tariq",        "adm.saif",   null,           "Domain Admins",     false },
        { 4729, "Usman Tariq",        "adm.saif",   null,           "Domain Admins",     false },
        { 4732, "Usman Tariq",        "adm.saif",   null,           "Backup Operators",  false },
        { 4733, "Usman Tariq",        "adm.saif",   null,           "Backup Operators",  false },
        { 4756, "Usman Tariq",        "adm.saif",   null,           "Enterprise Admins", false },
        { 4757, "Usman Tariq",        "adm.saif",   null,           "Enterprise Admins", false },
        { 4672, null,                 "adm.saif",   null,           null,                false },
        { 4768, "old.account",        null,         "10.10.31.90",  null,                true },
        { 4771, "bilal.ahmed",        null,         "10.10.31.77",  null,                true },
        { 4776, "administrator",      null,         null,           null,                true },
        { 4741, "WS-HR-12$",          "adm.saif",   null,           null,                false },
        { 4743, "WS-OLD-03$",         "adm.saif",   null,           null,                false },
        { 4719, null,                 "adm.saif",   null,           null,                false },
        { 1102, null,                 "adm.saif",   null,           null,                false },
        { 5136, null,                 "adm.saif",   null,           null,                false },
    };

    [Theory]
    [MemberData(nameof(Expectations))]
    public void Parses_sample_event(int id, string? target, string? subject, string? ip, string? group, bool failed)
    {
        var r = DcEventParser.Parse(Sample(id));
        Assert.NotNull(r);
        Assert.Equal(id, r.EventId);
        Assert.True(r.RecordId > 0);
        Assert.Equal(new DateTime(2026, 11, 18, 7, 42, 13, DateTimeKind.Utc), r.TimestampUtc.AddTicks(-(r.TimestampUtc.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal("DC01.corp.local", r.Computer);
        Assert.Equal(target, r.TargetUser);
        Assert.Equal(subject, r.SubjectUser);
        Assert.Equal(ip, r.SourceIp);
        Assert.Equal(group, r.GroupName);
        Assert.Equal(failed, r.Failed);
        Assert.False(string.IsNullOrWhiteSpace(DcEventParser.Describe(r)));
    }

    [Fact]
    public void Every_catalogued_event_id_has_a_sample()
    {
        var samples = Expectations.Select(row => (int)row[0]).ToHashSet();
        Assert.All(DcEventCatalog.Events.Keys, id => Assert.Contains(id, samples));
        Assert.Equal(26, samples.Count); // the 23 required IDs, with add/remove pairs counted separately
    }

    [Fact]
    public void Extracts_event_specific_fields()
    {
        var logon = DcEventParser.Parse(Sample(4624))!;
        Assert.Equal(10, logon.LogonType);
        Assert.Equal("DC01", logon.Workstation);

        var failed = DcEventParser.Parse(Sample(4625))!;
        Assert.Equal(3, failed.LogonType);
        Assert.Equal("WS-FIN-07", failed.Workstation);
        Assert.Equal("0xc000006d", failed.Status);
        Assert.Equal("0xc000006a", failed.SubStatus);
        Assert.Contains("wrong password", DcEventParser.Describe(failed));

        var lockout = DcEventParser.Parse(Sample(4740))!;
        Assert.Equal("WS-FIN-07", lockout.Workstation); // caller computer

        var gpo = DcEventParser.Parse(Sample(5136))!;
        Assert.Equal("CN=Default Domain Policy,CN=Policies,CN=System,DC=corp,DC=local", gpo.ObjectName);
        Assert.Contains("set versionNumber = 131085", gpo.Detail);

        var privileges = DcEventParser.Parse(Sample(4672))!;
        Assert.StartsWith("SeSecurityPrivilege, SeBackupPrivilege", privileges.Detail);

        Assert.Contains("account disabled, expired or locked (Kerberos)", DcEventParser.Describe(DcEventParser.Parse(Sample(4768))!));
        Assert.Contains("wrong password (Kerberos)", DcEventParser.Describe(DcEventParser.Parse(Sample(4771))!));
        Assert.Contains("unknown user name", DcEventParser.Describe(DcEventParser.Parse(Sample(4776))!));
        Assert.Equal("KALI-01", DcEventParser.Parse(Sample(4776))!.Workstation);
        Assert.Contains("added to Domain Admins", DcEventParser.Describe(DcEventParser.Parse(Sample(4728))!));
        Assert.Contains("removed from Domain Admins", DcEventParser.Describe(DcEventParser.Parse(Sample(4729))!));
    }

    [Theory]
    [InlineData("0xC000006A", "wrong password")]
    [InlineData("0xc0000064", "unknown user name")]
    [InlineData("0xc0000234", "account locked out")]
    [InlineData("0xc0000072", "account disabled")]
    [InlineData("0xc0000071", "password expired")]
    [InlineData("0x18", "wrong password (Kerberos)")]
    [InlineData("0x12", "account disabled, expired or locked (Kerberos)")]
    public void Decodes_failure_codes(string code, string text) => Assert.Equal(text, DcEventCatalog.StatusText(code));

    [Fact]
    public void Successful_logon_filter_ignores_known_sources()
    {
        var s = new DcSettings { SuccessfulLogonTypes = [], SuccessfulLogonIgnoreSources = ["10.10.20.0/24", "10.10.40.5", "APP-SRV-01"] };
        s.Normalise();
        var logon = DcEventParser.Parse(Sample(4624))!; // from 10.10.20.45
        Assert.False(DcResultProcessor.Keep(logon, s));
        Assert.False(DcResultProcessor.Keep(logon with { SourceIp = "10.10.40.5" }, s));
        Assert.False(DcResultProcessor.Keep(logon with { SourceIp = null, Workstation = "app-srv-01" }, s));
        Assert.True(DcResultProcessor.Keep(logon with { SourceIp = "10.10.21.9", Workstation = "WS-1" }, s));

        // The ignore list only applies to successful logons, never to failures.
        var failure = DcEventParser.Parse(Sample(4625))! with { SourceIp = "10.10.20.9" };
        Assert.True(DcResultProcessor.Keep(failure, s));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(40, 30)]
    public void Failed_security_reads_back_off(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), DcResultProcessor.SecurityBackoff(failures));

    [Fact]
    public void Forwarding_is_selectable_per_dc()
    {
        var s = new DcSettings { ForwardedDcs = ["DC02.corp.local", "10.0.0.13"] };
        s.Normalise();
        Assert.True(s.AnyForwarding);
        Assert.True(s.UsesForwarding("DC02", "10.0.0.12"));          // short name matches FQDN entry
        Assert.True(s.UsesForwarding("dc03.corp.local", "10.0.0.13")); // by IP
        Assert.False(s.UsesForwarding("DC01.corp.local", "10.0.0.11"));

        var all = new DcSettings { UseForwardedEvents = true };
        Assert.True(all.UsesForwarding("DC01", "10.0.0.11"));
        Assert.False(new DcSettings().AnyForwarding);
    }

    [Fact]
    public async Task Ingestion_is_duplicate_free_and_keeps_up_with_high_volume()
    {
        var root = Path.Combine(Path.GetTempPath(), "netwatch-wp1-" + Guid.NewGuid().ToString("N"));
        var paths = new NetWatchPaths(root);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNetWatchData(paths);
            await using var sp = services.BuildServiceProvider();
            await sp.InitialiseDatabasesAsync(NullLogger.Instance);

            var sink = new CapturingSink();
            var processor = new DcResultProcessor(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IMetricStore>(), sink,
                sp.GetRequiredService<SettingsService>(), null!, TimeProvider.System, NullLogger<DcResultProcessor>.Instance);
            var device = Dc(7);

            // 5,000 events = five minutes at 1,000 events/minute: 80 % successful logons, 20 % failures.
            var template4624 = DcEventParser.Parse(Sample(4624))!;
            var template4625 = DcEventParser.Parse(Sample(4625))!;
            var readings = Enumerable.Range(1, 5000)
                .Select(i => (i % 5 == 0 ? template4625 : template4624) with { RecordId = 1_000_000 + i, TimestampUtc = DateTime.UtcNow.AddSeconds(-5000 + i) })
                .ToList();

            var watch = Stopwatch.StartNew();
            var stored = 0;
            foreach (var chunk in readings.Chunk(1000))
            {
                using var scope = sp.CreateScope();
                stored += await processor.IngestSecurityAsync(device, chunk, scope.ServiceProvider.GetRequiredService<EventsDbContext>(), CancellationToken.None);
            }

            watch.Stop();
            Assert.Equal(5000, stored);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"5,000 events took {watch.Elapsed.TotalSeconds:F1} s");

            // Successful logons are stored but never sent to the alert engine.
            Assert.Equal(1000, sink.Signals.OfType<EventSignal>().Count(s => s.EventKey == EventKeys.DcSecurity));
            Assert.All(sink.Signals.OfType<EventSignal>(), s => Assert.Equal(4625, s.EventId));

            // A restart re-reads the last batch (bookmark not yet saved, or WEF overlap): nothing is stored twice.
            using (var scope = sp.CreateScope())
            {
                var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
                Assert.Equal(0, await processor.IngestSecurityAsync(device, readings.TakeLast(1000).ToList(), events, CancellationToken.None));
                Assert.Equal(5000, await events.DcSecurityEvents.CountAsync());
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static DeviceSnapshot Dc(int id)
    {
        var tags = new HashSet<string>();
        return new DeviceSnapshot(id, "DC01", "10.0.0.11", DeviceType.DomainController, 60, true, false, null, null, tags, new HashSet<int>(),
            new DeviceContext(id, "DC01", "10.0.0.11", DeviceType.DomainController, new HashSet<int>(), tags, false));
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
}
