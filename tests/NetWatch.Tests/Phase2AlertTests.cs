using NetWatch.Alerts.Engine;
using NetWatch.Alerts.Notifications;
using NetWatch.Alerts;
using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Monitoring;

namespace NetWatch.Tests;

/// <summary>Phase 2 alert engine: event filters, count-in-window, absence, suppression (maintenance, dependencies).</summary>
public class Phase2AlertTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

    private static DeviceContext Device(int id = 1, bool maintenance = false, bool enabled = true) =>
        new(id, $"SRV-{id}", $"10.0.0.{id}", DeviceType.Server, new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), maintenance, enabled);

    private static EventSignal Syslog(string message, int seconds, Severity severity = Severity.Info, string? app = null, int deviceId = 1) =>
        new(deviceId, EventKeys.SyslogMessage, message, T0.AddSeconds(seconds), severity, app);

    [Fact]
    public void EventMatch_FilterSelectsEvents()
    {
        var rule = new AlertRuleDefinition(1, "Critical syslog", AlertRuleType.EventMatch, Severity.Major, new RuleScope(), EventKeys.SyslogMessage,
            null, null, null, TimeSpan.Zero, new EventFilter { MinSeverity = Severity.Critical, Pattern = "fan|power" });
        var evaluator = new AlertEvaluator();
        var device = Device();

        Assert.Empty(evaluator.Evaluate(Syslog("Fan 1 failed", 0, Severity.Major), device, [rule]));       // severity too low
        Assert.Empty(evaluator.Evaluate(Syslog("Link down", 1, Severity.Critical), device, [rule]));       // pattern does not match
        var raise = Assert.Single(evaluator.Evaluate(Syslog("FAN 1 failed", 2, Severity.Critical), device, [rule]));
        Assert.Equal(TransitionKind.Raise, raise.Kind);
        Assert.False(raise.AutoResolve);
    }

    [Fact]
    public void EventFilter_MatchesTrapOidPrefixAndEventIds()
    {
        var trap = new EventSignal(1, EventKeys.SnmpTrap, "authenticationFailure", T0, Severity.Warning, "authenticationFailure", null, "1.3.6.1.6.3.1.1.5.5");
        Assert.True(new EventFilter { Oid = "1.3.6.1.6.3.1.1.5.5" }.Matches(trap));
        Assert.True(new EventFilter { Oid = ".1.3.6.1.6.3.1.1.5" }.Matches(trap));
        Assert.False(new EventFilter { Oid = "1.3.6.1.6.3.1.1.5.3" }.Matches(trap));

        var win = new EventSignal(1, EventKeys.WindowsEvent, "Service terminated", T0, Severity.Major, "Service Control Manager", 7031);
        Assert.True(new EventFilter { EventIds = [7031, 7034] }.Matches(win));
        Assert.False(new EventFilter { EventIds = [41] }.Matches(win));
        Assert.True(new EventFilter { Source = "control manager" }.Matches(win));
    }

    [Fact]
    public void EventFilter_InvalidRegexIsRejectedAndCorruptJsonMatchesNothing()
    {
        Assert.NotNull(new EventFilter { Pattern = "([" }.Validate());
        Assert.Null(new EventFilter { Pattern = "fail(ed)?" }.Validate());
        Assert.False(EventFilter.Parse("{not json").Matches(Syslog("anything", 0)));
        Assert.Null(new EventFilter().ToJson());
    }

    [Fact]
    public void CountInWindow_RaisesAtThresholdAndResolvesWhenQuiet()
    {
        var rule = new AlertRuleDefinition(2, "Brute force", AlertRuleType.CountInWindow, Severity.Warning, new RuleScope(), EventKeys.SyslogMessage,
            null, null, null, TimeSpan.Zero, new EventFilter { Pattern = "failed password" }, TimeSpan.FromMinutes(5), CountThreshold: 3);
        var evaluator = new AlertEvaluator();
        var device = Device();

        Assert.Empty(evaluator.Evaluate(Syslog("Failed password for root", 0), device, [rule]));
        Assert.Empty(evaluator.Evaluate(Syslog("Accepted password for admin", 5), device, [rule]));
        Assert.Empty(evaluator.Evaluate(Syslog("Failed password for root", 10), device, [rule]));
        var raise = Assert.Single(evaluator.Evaluate(Syslog("Failed password for admin", 20), device, [rule]));
        Assert.Equal(TransitionKind.Raise, raise.Kind);
        Assert.Contains("3 matching", raise.Message);

        // Still inside the window: nothing to resolve.
        Assert.Empty(evaluator.Tick(T0.AddMinutes(4), _ => device, [rule], _ => null));

        // The window has passed without matching events: resolve.
        var resolve = Assert.Single(evaluator.Tick(T0.AddMinutes(6), _ => device, [rule], _ => null));
        Assert.Equal(TransitionKind.Resolve, resolve.Kind);
    }

    [Fact]
    public void CountInWindow_OldEventsFallOutOfTheWindow()
    {
        var rule = new AlertRuleDefinition(2, "Burst", AlertRuleType.CountInWindow, Severity.Warning, new RuleScope(), EventKeys.SyslogMessage,
            null, null, null, TimeSpan.Zero, null, TimeSpan.FromMinutes(1), CountThreshold: 3);
        var evaluator = new AlertEvaluator();
        var device = Device();

        evaluator.Evaluate(Syslog("x", 0), device, [rule]);
        evaluator.Evaluate(Syslog("x", 30), device, [rule]);
        Assert.Empty(evaluator.Evaluate(Syslog("x", 70), device, [rule])); // the first event is older than 60 s
        Assert.Single(evaluator.Evaluate(Syslog("x", 80), device, [rule])); // 30, 70, 80 are within 60 s
    }

    [Fact]
    public void Absence_RaisesAfterSilenceAndResolvesWhenDataReturns()
    {
        var rule = new AlertRuleDefinition(3, "No CPU data", AlertRuleType.Absence, Severity.Warning, new RuleScope(), MetricKeys.Cpu,
            null, null, null, TimeSpan.FromMinutes(10));
        var evaluator = new AlertEvaluator();
        var device = Device();

        Assert.Empty(evaluator.Evaluate(new MetricSignal(1, null, MetricKeys.Cpu, null, 20, T0), device, [rule]));
        Assert.Empty(evaluator.Tick(T0.AddMinutes(9), _ => device, [rule], _ => null));
        var raise = Assert.Single(evaluator.Tick(T0.AddMinutes(11), _ => device, [rule], _ => null));
        Assert.Equal(TransitionKind.Raise, raise.Kind);
        Assert.Empty(evaluator.Tick(T0.AddMinutes(12), _ => device, [rule], _ => null)); // no duplicate

        var resolve = Assert.Single(evaluator.Evaluate(new MetricSignal(1, null, MetricKeys.Cpu, null, 25, T0.AddMinutes(13)), device, [rule]));
        Assert.Equal(TransitionKind.Resolve, resolve.Kind);
    }

    [Fact]
    public void Absence_IgnoresDisabledDevicesAndMaintenance()
    {
        var rule = new AlertRuleDefinition(3, "No syslog", AlertRuleType.Absence, Severity.Warning, new RuleScope(), EventKeys.SyslogMessage,
            null, null, null, TimeSpan.FromMinutes(5));
        var evaluator = new AlertEvaluator();
        evaluator.Evaluate(Syslog("hello", 0), Device(), [rule]);

        Assert.Empty(evaluator.Tick(T0.AddMinutes(10), _ => Device(enabled: false), [rule], _ => null));
        Assert.Empty(evaluator.Tick(T0.AddMinutes(10), _ => Device(maintenance: true), [rule], _ => null));
        Assert.Single(evaluator.Tick(T0.AddMinutes(10), _ => Device(), [rule], _ => null));
    }

    [Fact]
    public void Suppression_UpstreamUnreachableDelaysTheAlertUntilItIsReachable()
    {
        var rule = new AlertRuleDefinition(4, "Device down", AlertRuleType.StateChange, Severity.Critical, new RuleScope(), StateKeys.DeviceDown,
            null, null, null, TimeSpan.Zero);
        var evaluator = new AlertEvaluator();
        var device = Device(5);
        StateSignal Down(int minutes) => new(5, null, StateKeys.DeviceDown, null, true, "down", T0.AddMinutes(minutes));

        // The core switch is unreachable: the access device's down state is a consequence.
        Assert.Empty(evaluator.Evaluate(Down(0), device, [rule], suppressedBy: "CORE-SW-01"));
        Assert.Contains(evaluator.Suppressed, s => s.DeviceId == 5 && s.Reason.Contains("CORE-SW-01"));

        // The core is back but this device is still down: now it is a real problem.
        var raise = Assert.Single(evaluator.Evaluate(Down(1), device, [rule], suppressedBy: null));
        Assert.Equal(TransitionKind.Raise, raise.Kind);
        Assert.Empty(evaluator.Suppressed);
    }

    [Fact]
    public void Suppression_MaintenanceBlocksEventAlertsToo()
    {
        var rule = new AlertRuleDefinition(5, "Reboot", AlertRuleType.EventMatch, Severity.Warning, new RuleScope(), EventKeys.DeviceReboot,
            null, null, null, TimeSpan.Zero);
        var evaluator = new AlertEvaluator();
        var reboot = new EventSignal(1, EventKeys.DeviceReboot, "rebooted", T0);
        Assert.Empty(evaluator.Evaluate(reboot, Device(maintenance: true), [rule]));
        Assert.Single(evaluator.Evaluate(reboot, Device(), [rule]));
    }

    [Fact]
    public void Threshold_PerVolumeInstancesAreIndependent()
    {
        var rule = new AlertRuleDefinition(6, "Disk low", AlertRuleType.Threshold, Severity.Warning, new RuleScope(), MetricKeys.DiskUsed,
            ComparisonOperator.GreaterThan, 85, 80, TimeSpan.Zero);
        var evaluator = new AlertEvaluator();
        var device = Device();
        var c = Assert.Single(evaluator.Evaluate(new MetricSignal(1, null, MetricKeys.DiskUsed, "C:", 90, T0, "C:"), device, [rule]));
        Assert.Contains("C:", c.Title);
        Assert.Empty(evaluator.Evaluate(new MetricSignal(1, null, MetricKeys.DiskUsed, "D:", 50, T0), device, [rule]));
        var d = Assert.Single(evaluator.Evaluate(new MetricSignal(1, null, MetricKeys.DiskUsed, "D:", 91, T0.AddMinutes(1), "D:"), device, [rule]));
        Assert.NotEqual(c.DedupKey, d.DedupKey);
    }

    [Fact]
    public void RuleDefinition_SameConditionDetectsFilterChanges()
    {
        var a = new AlertRuleDefinition(1, "r", AlertRuleType.EventMatch, Severity.Info, new RuleScope(), EventKeys.SyslogMessage, null, null, null, TimeSpan.Zero, new EventFilter { Pattern = "a" });
        var b = a with { Name = "renamed", Severity = Severity.Critical };
        var c = a with { Filter = new EventFilter { Pattern = "b" } };
        Assert.True(a.SameCondition(b));
        Assert.False(a.SameCondition(c));
    }

    [Fact]
    public void Notifications_SyslogForwardEscapesStructuredData()
    {
        var now = T0;
        var dto = new AlertDto(7, 1, 1, "SW \"core\" ]1", null, Severity.Critical, AlertState.Active, "Device down: SW", "line1\nline2", null, true,
            now, now, 1, null, null, null, null, null, null, false, 0, null);
        var text = SyslogForwardChannel.Format(new AlertNotification(NotificationKind.Raised, dto), "NWHOST");
        Assert.StartsWith("<130>1 ", text); // local0 (16*8) + critical (2)
        Assert.Contains("device=\"SW \\\"core\\\" \\]1\"", text);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public void Notifications_DigestSummarisesBursts()
    {
        var now = T0;
        AlertNotification N(NotificationKind kind, Severity s, string title) =>
            new(kind, new AlertDto(1, 1, 1, "d", null, s, AlertState.Active, title, "m", null, true, now, now, 1, null, null, null, null, null, null, false, 0, null));
        var (subject, body) = NotificationDispatcher.Digest([N(NotificationKind.Raised, Severity.Warning, "a"), N(NotificationKind.Raised, Severity.Critical, "b"), N(NotificationKind.Resolved, Severity.Major, "c")]);
        Assert.Contains("3 alert notifications", subject);
        Assert.Contains("worst Critical", subject);
        // Worst first.
        Assert.True(body.IndexOf("Critical", StringComparison.Ordinal) < body.IndexOf("Warning", StringComparison.Ordinal));
    }
}
