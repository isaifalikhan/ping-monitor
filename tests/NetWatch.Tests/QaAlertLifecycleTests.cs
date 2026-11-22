using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Alerts;
using NetWatch.Alerts.Engine;
using NetWatch.Alerts.Notifications;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data;
using NetWatch.Data.Config;

namespace NetWatch.Tests;

/// <summary>
/// Release QA (TEST_PLAN ALR-10…ALR-19): the real alert engine (AlertManager) with a real database and a manual
/// clock: thresholds fire at the limit and not just below, respect the duration, auto-resolve with hysteresis,
/// de-duplicate, honour dependencies, maintenance and escalation, survive a restart, and the operator lifecycle
/// (acknowledge with comment → resolve → close) is recorded in the history.
/// </summary>
public sealed class QaAlertLifecycleTests : IAsyncLifetime
{
    // In the past: acknowledge/close use the wall clock, signals use the manual clock.
    private static readonly DateTime T0 = new DateTime(DateTime.UtcNow.AddHours(-3).Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-qa-alerts-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(T0);
    private readonly Devices _devices = new();
    private readonly Dependencies _dependencies = new();
    private readonly Sink _sink = new();
    private ServiceProvider _sp = null!;
    private AlertManager _manager = null!;
    private CancellationTokenSource _cts = new();

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(new NetWatchPaths(_root));
        _sp = services.BuildServiceProvider();
        await _sp.InitialiseDatabasesAsync(NullLogger.Instance);
        for (var i = 1; i <= 6; i++)
        {
            _devices.Add(new DeviceContext(i, $"DEV-{i}", $"10.0.0.{i}", DeviceType.Server, new HashSet<int>(), new HashSet<string>(), false));
        }

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        for (var i = 1; i <= 6; i++)
        {
            db.Devices.Add(new Device { Id = i, Name = $"DEV-{i}", IpAddress = $"10.0.0.{i}", Type = DeviceType.Server, CreatedUtc = T0, StatusSinceUtc = T0 });
        }

        db.AlertRules.AddRange(
            new AlertRule { Id = 100, Name = "CPU high", RuleType = AlertRuleType.Threshold, Severity = Severity.Major, Key = "cpu.pct", Operator = ComparisonOperator.GreaterThan, Threshold = 90, ClearThreshold = 80, DurationSec = 120, CreatedUtc = T0, UpdatedUtc = T0 },
            new AlertRule { Id = 101, Name = "Disk free low", RuleType = AlertRuleType.Threshold, Severity = Severity.Warning, Key = "disk.free.gb", Operator = ComparisonOperator.LessOrEqual, Threshold = 10, DurationSec = 0, CreatedUtc = T0, UpdatedUtc = T0 },
            new AlertRule { Id = 102, Name = "Device down", RuleType = AlertRuleType.StateChange, Severity = Severity.Critical, Key = "device.down", DurationSec = 0, EscalateAfterMin = 5, CreatedUtc = T0, UpdatedUtc = T0 });
        await db.SaveChangesAsync();
        await StartEngineAsync();
    }

    private async Task StartEngineAsync()
    {
        _cts = new CancellationTokenSource();
        _manager = new AlertManager(_sp.GetRequiredService<IServiceScopeFactory>(), _devices, _dependencies, new NoLive(), _sink,
            _sp.GetRequiredService<SettingsService>(), _clock, NullLogger<AlertManager>.Instance);
        await _manager.StartAsync(_cts.Token);
        await _manager.Ready;
    }

    private async Task StopEngineAsync()
    {
        await _cts.CancelAsync();
        await _manager.StopAsync(CancellationToken.None);
        _manager.Dispose();
    }

    public async Task DisposeAsync()
    {
        await StopEngineAsync();
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

    private static MetricSignal Cpu(int device, double value, int seconds) => new(device, null, "cpu.pct", null, value, T0.AddSeconds(seconds));

    private static StateSignal Down(int device, bool problem, int seconds) => new(device, null, "device.down", null, problem, problem ? "No answer to ping" : "Answers again", T0.AddSeconds(seconds));

    /// <summary>Publishes signals and waits until the engine has processed all of them.</summary>
    private async Task FeedAsync(params AlertSignal[] signals)
    {
        var target = _manager.ProcessedSignals + signals.Length;
        foreach (var s in signals)
        {
            _manager.Publish(s);
        }

        for (var i = 0; i < 400 && _manager.ProcessedSignals < target; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(_manager.ProcessedSignals >= target, "engine did not process the signals");
        await Task.Delay(50); // persistence after the counter
    }

    /// <summary>Advances the clock past the next periodic check and wakes the engine with a signal for an unknown device.</summary>
    private async Task TickAsync(TimeSpan advance)
    {
        _clock.Advance(advance);
        await FeedAsync(new MetricSignal(9999, null, "cpu.pct", null, 0, _clock.GetUtcNow().UtcDateTime));
        await Task.Delay(150);
    }

    private async Task<List<Alert>> AlertsAsync(int? deviceId = null)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        return await db.Alerts.AsNoTracking().Include(a => a.History).Where(a => deviceId == null || a.DeviceId == deviceId).OrderBy(a => a.Id).ToListAsync();
    }

    /// <summary>
    /// ProcessedSignals must not run ahead of the database: once it covers a signal, the alert that signal raised or
    /// resolved is stored. (It used to be counted before persisting, which made these tests fail intermittently under load.)
    /// </summary>
    [Fact]
    public async Task Processed_signal_count_never_runs_ahead_of_the_stored_alerts()
    {
        async Task PublishAndWaitForCounterOnly(AlertSignal signal)
        {
            var target = _manager.ProcessedSignals + 1;
            _manager.Publish(signal);
            for (var i = 0; i < 1000 && _manager.ProcessedSignals < target; i++)
            {
                await Task.Delay(2);
            }

            Assert.True(_manager.ProcessedSignals >= target, "engine did not process the signal");
        }

        // A new disk each round: raising and resolving the same alert every second would (correctly) count as flapping.
        for (var round = 0; round < 25; round++)
        {
            await PublishAndWaitForCounterOnly(new MetricSignal(6, null, "disk.free.gb", $"V{round}:", 5, T0.AddSeconds((round * 2) + 1)));
            Assert.Equal(round + 1, (await AlertsAsync(6)).Count);
            Assert.Single(await AlertsAsync(6), a => a.State == AlertState.Active);

            await PublishAndWaitForCounterOnly(new MetricSignal(6, null, "disk.free.gb", $"V{round}:", 50, T0.AddSeconds((round * 2) + 2)));
            Assert.DoesNotContain(await AlertsAsync(6), a => a.State == AlertState.Active);
        }
    }

    [Fact]
    public async Task Threshold_fires_only_after_duration_not_at_limit_and_resolves_with_hysteresis()
    {
        // Exactly at the limit (GreaterThan 90) for 10 minutes: never fires.
        await FeedAsync(Enumerable.Range(0, 11).Select(i => (AlertSignal)Cpu(2, 90.0, i * 60)).ToArray());
        Assert.Empty(await AlertsAsync(2));

        // Above the limit, but not yet for the full 120 s.
        await FeedAsync(Cpu(1, 95, 0), Cpu(1, 96, 60), Cpu(1, 97, 119));
        Assert.Empty(await AlertsAsync(1));

        // 120 s reached: raised once, with a Raised notification.
        await FeedAsync(Cpu(1, 97, 120), Cpu(1, 98, 180));
        var alert = Assert.Single(await AlertsAsync(1));
        Assert.Equal(AlertState.Active, alert.State);
        Assert.Equal(Severity.Major, alert.Severity);
        Assert.Contains(_sink.Items, n => n.Kind == NotificationKind.Raised && n.Alert.Id == alert.Id);

        // Below the limit but above the clear threshold (hysteresis): stays open.
        await FeedAsync(Cpu(1, 85, 240), Cpu(1, 80.5, 300));
        Assert.Equal(AlertState.Active, Assert.Single(await AlertsAsync(1)).State);

        // At the clear threshold: resolved, with a Resolved notification and history.
        await FeedAsync(Cpu(1, 80, 360));
        alert = Assert.Single(await AlertsAsync(1));
        Assert.Equal(AlertState.Resolved, alert.State);
        Assert.Equal(T0.AddSeconds(360), alert.ResolvedUtc);
        Assert.Contains(alert.History, h => h.Action == "Resolved");
        Assert.Contains(_sink.Items, n => n.Kind == NotificationKind.Resolved && n.Alert.Id == alert.Id);
    }

    [Fact]
    public async Task Threshold_dip_below_limit_restarts_the_duration()
    {
        await FeedAsync(Cpu(3, 95, 0), Cpu(3, 95, 100), Cpu(3, 70, 110), Cpu(3, 95, 130), Cpu(3, 95, 240));
        Assert.Empty(await AlertsAsync(3)); // 110 s since the dip, below 120 s
        await FeedAsync(Cpu(3, 95, 251));
        Assert.Single(await AlertsAsync(3));
    }

    [Fact]
    public async Task LessOrEqual_operator_fires_at_the_limit_and_not_just_above()
    {
        await FeedAsync(new MetricSignal(4, null, "disk.free.gb", "C:", 10.01, T0, "C:"));
        Assert.Empty(await AlertsAsync(4));
        await FeedAsync(new MetricSignal(4, null, "disk.free.gb", "C:", 10.0, T0.AddSeconds(60), "C:"));
        Assert.Single(await AlertsAsync(4));

        // Another volume of the same device is a separate alert (per-instance de-duplication).
        await FeedAsync(new MetricSignal(4, null, "disk.free.gb", "D:", 5, T0.AddSeconds(60), "D:"));
        Assert.Equal(2, (await AlertsAsync(4)).Count);
    }

    [Fact]
    public async Task Repeated_problem_does_not_create_duplicates_and_closing_allows_a_new_alert()
    {
        await FeedAsync(Down(5, true, 0), Down(5, true, 60), Down(5, true, 120));
        var alert = Assert.Single(await AlertsAsync(5));
        Assert.Equal(AlertState.Active, alert.State);

        // Operator lifecycle: acknowledge with a comment, comment, close while still open.
        using (var scope = _sp.CreateScope())
        {
            var service = new AlertService(scope.ServiceProvider.GetRequiredService<NetWatchDbContext>(), _manager, new NoLive());
            var actor = new AlertActor(1, "qa-operator");
            Assert.Equal(1, await service.AcknowledgeAsync([alert.Id], actor, "Looking at it <script>x</script>", CancellationToken.None));
            Assert.Equal(0, await service.AcknowledgeAsync([alert.Id], actor, "again", CancellationToken.None)); // already acknowledged
            Assert.True(await service.CommentAsync(alert.Id, actor, "Cable replaced", CancellationToken.None));
            Assert.False(await service.CommentAsync(999_999, actor, "x", CancellationToken.None));

            // Open, auto-resolving alert: not closed unless explicitly included.
            Assert.Equal(0, await service.CloseAsync([alert.Id], actor, "no", includeOpen: false, CancellationToken.None));
            Assert.Equal(1, await service.CloseAsync([alert.Id], actor, "Closed by QA", includeOpen: true, CancellationToken.None));
            var detail = await service.GetAsync(alert.Id, CancellationToken.None);
            Assert.NotNull(detail);
            Assert.Equal(["Raised", "Acknowledged", "Commented", "Closed"], detail.Value.History.Select(h => h.Action).ToArray());
            Assert.Equal("Looking at it <script>x</script>", detail.Value.Alert.AckComment); // stored verbatim; the UI renders text, not HTML
            Assert.Equal("qa-operator", detail.Value.Alert.ClosedByUsername);

            var page = await service.ListAsync(new AlertQuery(null, null, null, null, null, null, 1, 50), CancellationToken.None);
            Assert.Contains(page.Items, a => a.Id == alert.Id);
        }

        // The condition is still present: after closing, the next problem signal opens a NEW alert.
        await FeedAsync(Down(5, false, 180), Down(5, true, 240));
        var all = await AlertsAsync(5);
        Assert.Equal(2, all.Count);
        Assert.Equal(AlertState.Closed, all[0].State);
        Assert.Equal(AlertState.Active, all[1].State);
    }

    [Fact]
    public async Task Unreachable_upstream_suppresses_and_maintenance_suppresses_new_alerts()
    {
        _dependencies.Unreachable[6] = "CORE-SW-01";
        await FeedAsync(Down(6, true, 0));
        Assert.Empty(await AlertsAsync(6));
        await TickAsync(TimeSpan.FromSeconds(20));
        Assert.Contains(_manager.SuppressedConditions, s => s.DeviceId == 6);

        // Upstream back: the child's own problem now raises.
        _dependencies.Unreachable.Clear();
        await FeedAsync(Down(6, false, 30), Down(6, true, 60));
        Assert.Single(await AlertsAsync(6));

        // Maintenance: no new alert for device 2.
        _devices.Add(_devices.Get(2)! with { InMaintenance = true });
        await FeedAsync(Down(2, true, 0));
        Assert.Empty(await AlertsAsync(2));
    }

    [Fact]
    public async Task Unacknowledged_critical_alert_escalates_after_its_rule_time()
    {
        _clock.Set(T0);
        await FeedAsync(Down(1, true, 0));
        var alert = Assert.Single(await AlertsAsync(1));
        await TickAsync(TimeSpan.FromMinutes(3));
        Assert.Null((await AlertsAsync(1))[0].EscalatedUtc);
        await TickAsync(TimeSpan.FromMinutes(3)); // 6 min > EscalateAfterMin 5
        alert = Assert.Single(await AlertsAsync(1));
        Assert.NotNull(alert.EscalatedUtc);
        Assert.Contains(alert.History, h => h.Action == "Escalated");
        Assert.Contains(_sink.Items, n => n.Kind == NotificationKind.Escalated && n.Alert.Id == alert.Id);
        Assert.Equal(Severity.Critical, _manager.GetWorstSeverity(1));
    }

    [Fact]
    public async Task Open_alerts_survive_an_engine_restart_and_still_auto_resolve()
    {
        await FeedAsync(Down(3, true, 0));
        Assert.Single(await AlertsAsync(3));
        await StopEngineAsync();
        await StartEngineAsync();
        Assert.Equal(Severity.Critical, _manager.GetWorstSeverity(3));
        await FeedAsync(Down(3, false, 60));
        Assert.Equal(AlertState.Resolved, Assert.Single(await AlertsAsync(3)).State);
        Assert.Null(_manager.GetWorstSeverity(3));
    }

    [Fact]
    public async Task Flapping_condition_is_held_open_and_resolves_once_stable()
    {
        // Default flap threshold 5 changes within the window: the 5th change (a clear) is held.
        await FeedAsync(Down(4, true, 0), Down(4, false, 10), Down(4, true, 20), Down(4, false, 30), Down(4, true, 40), Down(4, false, 50));
        var alerts = await AlertsAsync(4);
        var open = Assert.Single(alerts, a => a.State is AlertState.Active);
        Assert.True(open.IsFlapping);
        Assert.Contains(open.History, h => h.Action == "Flapping");

        var stable = _sp.GetRequiredService<SettingsService>().Alerting.FlapStableMin;
        await TickAsync(TimeSpan.FromMinutes(stable + 1));
        Assert.All(await AlertsAsync(4), a => Assert.NotEqual(AlertState.Active, a.State));
    }

    [Fact]
    public async Task Signals_for_a_device_deleted_meanwhile_are_dropped_and_the_engine_keeps_working()
    {
        // BUG-005 regression: the alert cannot be stored (foreign key); nothing is raised, later signals still work.
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            db.Devices.Remove(await db.Devices.FirstAsync(d => d.Id == 6));
            await db.SaveChangesAsync();
        }

        await FeedAsync(Down(6, true, 0));
        Assert.Empty(await AlertsAsync(6));
        await FeedAsync(Down(5, true, 10));
        Assert.Single(await AlertsAsync(5));
    }

    private sealed class ManualClock(DateTime start) : TimeProvider
    {
        private long _ticks = start.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public void Set(DateTime utc) => Interlocked.Exchange(ref _ticks, utc.Ticks);
    }

    private sealed class Devices : IDeviceContextProvider
    {
        private readonly ConcurrentDictionary<int, DeviceContext> _all = new();

        public void Add(DeviceContext d) => _all[d.Id] = d;

        public DeviceContext? Get(int deviceId) => _all.GetValueOrDefault(deviceId);
    }

    private sealed class Dependencies : IDependencyStatus
    {
        public ConcurrentDictionary<int, string> Unreachable { get; } = new();

        public string? FindUnreachableUpstream(int deviceId) => Unreachable.GetValueOrDefault(deviceId);
    }

    private sealed class Sink : INotificationSink
    {
        public ConcurrentQueue<AlertNotification> Items { get; } = new();

        public void Enqueue(AlertNotification notification) => Items.Enqueue(notification);
    }

    private sealed class NoLive : ILiveUpdatePublisher
    {
        public Task PublishAsync(string eventName, object payload, string? group = null) => Task.CompletedTask;
    }
}
