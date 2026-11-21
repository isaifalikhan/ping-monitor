using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Alerts;
using NetWatch.Alerts.Engine;
using NetWatch.Collectors.DomainControllers;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Processing;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Data.Events;

namespace NetWatch.Tests;

/// <summary>
/// WP2: every built-in DC security rule, driven end to end: sample Security events → DcResultProcessor (filters,
/// fields such as failed/privileged/offHours/source) → AlertEvaluator with the shipped default rules.
/// </summary>
public sealed class Wp2DcRuleTests : IAsyncLifetime
{
    private static readonly DateTime WorkTime = new(2026, 11, 18, 7, 0, 0, DateTimeKind.Utc);   // Wed 12:00 PKT
    private static readonly DateTime OffHours = new(2026, 11, 22, 22, 0, 0, DateTimeKind.Utc);  // Mon 03:00 PKT

    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-wp2-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;
    private DcResultProcessor _processor = null!;
    private readonly Sink _sink = new();
    private long _record = 5_000_000;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(new NetWatchPaths(_root));
        _sp = services.BuildServiceProvider();
        await _sp.InitialiseDatabasesAsync(NullLogger.Instance);
        _processor = new DcResultProcessor(_sp.GetRequiredService<IServiceScopeFactory>(), _sp.GetRequiredService<IMetricStore>(), _sink,
            _sp.GetRequiredService<SettingsService>(), null!, TimeProvider.System, NullLogger<DcResultProcessor>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DcSecurityReading Sample(int id) =>
        DcEventParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "Dc", $"{id}.xml")))!;

    /// <summary>Ingests the readings (fresh record IDs) and returns the titles of all alerts the default rules raise.</summary>
    private async Task<List<AlertTransition>> RunAsync(params DcSecurityReading[] readings)
    {
        _sink.Signals.Clear();
        var device = Dc();
        using (var scope = _sp.CreateScope())
        {
            var batch = readings.Select(r => r with { RecordId = ++_record }).ToList();
            await _processor.IngestSecurityAsync(device, batch, scope.ServiceProvider.GetRequiredService<EventsDbContext>(), CancellationToken.None);
        }

        var rules = DefaultRules.Create(DateTime.UtcNow).Select((r, i) =>
        {
            r.Id = i + 1;
            return AlertRuleDefinition.From(r);
        }).Where(r => r.Key == Core.Monitoring.EventKeys.DcSecurity).ToList();
        var evaluator = new AlertEvaluator();
        return _sink.Signals.SelectMany(s => evaluator.Evaluate(s, device.Context, rules)).Where(t => t.Kind == TransitionKind.Raise).ToList();
    }

    private static List<string> Rules(IEnumerable<AlertTransition> raised) => raised.Select(t => t.Rule.Name).Distinct().ToList();

    [Fact]
    public async Task Five_failed_logons_for_one_account_in_five_minutes()
    {
        var f = Sample(4625);
        var raised = await RunAsync(Enumerable.Range(0, 5).Select(i => f with { TimestampUtc = WorkTime.AddSeconds(i * 50) }).ToArray());
        var alert = Assert.Single(raised);
        Assert.Equal("Failed logons for one account", alert.Rule.Name);
        Assert.Contains("bilal.ahmed", alert.Title);

        // Four in five minutes, or five spread over ten minutes, stay quiet.
        Assert.Empty(await RunAsync(Enumerable.Range(0, 4).Select(i => f with { TargetUser = "u4", TimestampUtc = WorkTime.AddSeconds(i * 50) }).ToArray()));
        Assert.Empty(await RunAsync(Enumerable.Range(0, 5).Select(i => f with { TargetUser = "u5", TimestampUtc = WorkTime.AddMinutes(i * 2) }).ToArray()));
    }

    [Fact]
    public async Task Twenty_failed_logons_from_one_source_ip_is_brute_force()
    {
        var f = Sample(4625);
        var raised = await RunAsync(Enumerable.Range(0, 20).Select(i => f with { TargetUser = $"user{i}", TimestampUtc = WorkTime.AddSeconds(i * 10) }).ToArray());
        var alert = Assert.Single(raised);
        Assert.Equal("Failed logons from one source", alert.Rule.Name);
        Assert.Equal(Severity.Major, alert.Rule.Severity);
        Assert.Contains("10.10.31.77", alert.Title);
    }

    [Fact]
    public async Task Lockout_raises_immediately() =>
        Assert.Equal(["Account locked out"], Rules(await RunAsync(Sample(4740) with { TimestampUtc = WorkTime })));

    [Theory]
    [InlineData(4728, "Domain Admins")]
    [InlineData(4756, "Enterprise Admins")]
    [InlineData(4732, "Administrators")]
    [InlineData(4732, "Backup Operators")]
    [InlineData(4732, "Account Operators")]
    [InlineData(4756, "Schema Admins")]
    public async Task Privileged_group_addition_is_critical(int eventId, string group)
    {
        var raised = await RunAsync(Sample(eventId) with { GroupName = group, TimestampUtc = WorkTime });
        var alert = Assert.Single(raised);
        Assert.Equal("Privileged group membership added", alert.Rule.Name);
        Assert.Equal(Severity.Critical, alert.Rule.Severity);
    }

    [Fact]
    public async Task Ordinary_group_changes_and_removals_do_not_raise()
    {
        Assert.Empty(await RunAsync(Sample(4728) with { GroupName = "HR Staff", TimestampUtc = WorkTime }));
        Assert.Empty(await RunAsync(Sample(4729) with { TimestampUtc = WorkTime })); // removal from Domain Admins
    }

    [Fact]
    public async Task Log_cleared_and_audit_policy_change_are_critical()
    {
        var raised = await RunAsync(Sample(1102) with { TimestampUtc = WorkTime }, Sample(4719) with { TimestampUtc = WorkTime });
        Assert.Equal(["Security log cleared", "Audit policy changed"], Rules(raised));
        Assert.All(raised, t => Assert.Equal(Severity.Critical, t.Rule.Severity));
    }

    [Fact]
    public async Task Admin_logon_only_alerts_outside_working_hours()
    {
        Assert.Empty(await RunAsync(Sample(4672) with { TimestampUtc = WorkTime }));
        Assert.Equal(["Admin logon outside working hours"], Rules(await RunAsync(Sample(4672) with { TimestampUtc = OffHours })));
        Assert.Equal(["Admin logon outside working hours"], Rules(await RunAsync(Sample(4672) with { TimestampUtc = new DateTime(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc) }))); // Saturday
    }

    [Fact]
    public async Task Account_creation_and_deletion_raise()
    {
        Assert.Equal(["New user account created"], Rules(await RunAsync(Sample(4720) with { TimestampUtc = WorkTime })));
        Assert.Equal(["New computer account created"], Rules(await RunAsync(Sample(4741) with { TimestampUtc = WorkTime })));
        Assert.Equal(["User or computer account deleted"], Rules(await RunAsync(Sample(4726) with { TimestampUtc = WorkTime })));
        Assert.Equal(["User or computer account deleted"], Rules(await RunAsync(Sample(4743) with { TimestampUtc = WorkTime })));
    }

    [Fact]
    public async Task Kerberos_preauth_failure_spike_across_accounts()
    {
        var k = Sample(4771);
        // 50 different accounts from 50 different clients: no per-account or per-source rule fires, the spike rule does.
        var raised = await RunAsync(Enumerable.Range(0, 50)
            .Select(i => k with { TargetUser = $"user{i}", SourceIp = $"10.20.{i / 200}.{i % 200 + 1}", TimestampUtc = WorkTime.AddSeconds(i * 5) }).ToArray());
        Assert.Equal(["Kerberos pre-authentication failures spike"], Rules(raised));
        Assert.Empty(await RunAsync(Enumerable.Range(0, 49).Select(i => k with { TargetUser = $"x{i}", SourceIp = $"10.30.0.{i + 1}", TimestampUtc = OffHours.AddSeconds(i) }).ToArray()));
    }

    [Fact]
    public async Task Successful_logons_never_alert()
    {
        var s = Sample(4624);
        Assert.Empty(await RunAsync(Enumerable.Range(0, 200).Select(i => s with { TimestampUtc = OffHours.AddSeconds(i) }).ToArray()));
        Assert.Empty(_sink.Signals);
    }

    [Fact]
    public void Every_dc_security_rule_is_covered_by_a_test()
    {
        var covered = new[]
        {
            "Failed logons for one account", "Failed logons from one source", "Account locked out", "Privileged group membership added",
            "Security log cleared", "Audit policy changed", "Admin logon outside working hours", "New user account created",
            "New computer account created", "User or computer account deleted", "Kerberos pre-authentication failures spike",
        };
        var shipped = DefaultRules.Create(DateTime.UtcNow).Where(r => r.Key == Core.Monitoring.EventKeys.DcSecurity).Select(r => r.Name).ToList();
        Assert.Equal(covered.Order(), shipped.Order());
        Assert.Contains("Account Operators", new DcSettings().PrivilegedGroups);
    }

    private static DeviceSnapshot Dc()
    {
        var tags = new HashSet<string>();
        return new DeviceSnapshot(9, "DC01", "10.0.0.11", DeviceType.DomainController, 60, true, false, null, null, tags, new HashSet<int>(),
            new DeviceContext(9, "DC01", "10.0.0.11", DeviceType.DomainController, new HashSet<int>(), tags, false));
    }

    private sealed class Sink : IAlertSignalSink
    {
        public List<AlertSignal> Signals { get; } = new();

        public void Publish(AlertSignal signal) => Signals.Add(signal);
    }
}
