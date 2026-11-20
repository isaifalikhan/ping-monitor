using NetWatch.Alerts.Engine;
using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Monitoring;

namespace NetWatch.Tests;

public class AlertEvaluatorTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);

    private static DeviceContext Device(int id = 1, bool maintenance = false, DeviceType type = DeviceType.Switch, params int[] groups) =>
        new(id, $"SW-{id}", $"10.0.0.{id}", type, groups.ToHashSet(), new HashSet<string>(["core"], StringComparer.OrdinalIgnoreCase), maintenance);

    private static AlertRuleDefinition CpuRule(int durationSec = 300, double? clear = 85) =>
        new(1, "High CPU", AlertRuleType.Threshold, Severity.Major, new RuleScope(), MetricKeys.Cpu, ComparisonOperator.GreaterThan, 90, clear, TimeSpan.FromSeconds(durationSec));

    private static MetricSignal Cpu(double value, int minutes, int deviceId = 1) =>
        new(deviceId, null, MetricKeys.Cpu, null, value, T0.AddMinutes(minutes));

    [Fact]
    public void Threshold_RaisesOnlyAfterDuration()
    {
        var evaluator = new AlertEvaluator();
        var rules = new[] { CpuRule() };
        var device = Device();

        Assert.Empty(evaluator.Evaluate(Cpu(95, 0), device, rules));
        Assert.Empty(evaluator.Evaluate(Cpu(96, 2), device, rules));
        Assert.Empty(evaluator.Evaluate(Cpu(97, 4), device, rules));
        var raised = Assert.Single(evaluator.Evaluate(Cpu(98, 5), device, rules));
        Assert.Equal(TransitionKind.Raise, raised.Kind);
        Assert.Equal(Severity.Major, raised.Rule.Severity);
        Assert.Contains("98", raised.Message);

        // No duplicate raise while the condition persists.
        Assert.Empty(evaluator.Evaluate(Cpu(99, 6), device, rules));
    }

    [Fact]
    public void Threshold_DipBelowThresholdRestartsTheTimer()
    {
        var evaluator = new AlertEvaluator();
        var rules = new[] { CpuRule() };
        var device = Device();

        evaluator.Evaluate(Cpu(95, 0), device, rules);
        evaluator.Evaluate(Cpu(50, 3), device, rules);
        Assert.Empty(evaluator.Evaluate(Cpu(95, 4), device, rules));
        Assert.Empty(evaluator.Evaluate(Cpu(95, 8), device, rules));
        Assert.Single(evaluator.Evaluate(Cpu(95, 9), device, rules));
    }

    [Fact]
    public void Threshold_HysteresisDelaysResolve()
    {
        var evaluator = new AlertEvaluator();
        var rules = new[] { CpuRule(durationSec: 0, clear: 80) };
        var device = Device();

        Assert.Single(evaluator.Evaluate(Cpu(95, 0), device, rules));

        // 85 is below the threshold but above the clear level: still raised.
        Assert.Empty(evaluator.Evaluate(Cpu(85, 1), device, rules));
        var resolved = Assert.Single(evaluator.Evaluate(Cpu(79, 2), device, rules));
        Assert.Equal(TransitionKind.Resolve, resolved.Kind);
    }

    [Fact]
    public void Threshold_WithoutClearThreshold_ResolvesWhenBelowThreshold()
    {
        var evaluator = new AlertEvaluator();
        var rules = new[] { CpuRule(durationSec: 0, clear: null) };
        evaluator.Evaluate(Cpu(95, 0), Device(), rules);
        Assert.Equal(TransitionKind.Resolve, Assert.Single(evaluator.Evaluate(Cpu(89, 1), Device(), rules)).Kind);
    }

    [Fact]
    public void Maintenance_SuppressesNewAlertsButAllowsResolve()
    {
        var evaluator = new AlertEvaluator();
        var rules = new[] { CpuRule(durationSec: 0) };

        Assert.Empty(evaluator.Evaluate(Cpu(99, 0), Device(maintenance: true), rules));

        // Raised before maintenance started → resolves normally.
        Assert.Single(evaluator.Evaluate(Cpu(99, 1, deviceId: 2), Device(2), rules));
        Assert.Single(evaluator.Evaluate(Cpu(10, 2, deviceId: 2), Device(2, maintenance: true), rules));
    }

    [Fact]
    public void StateChange_RaisesAndResolves()
    {
        var evaluator = new AlertEvaluator();
        var rule = new AlertRuleDefinition(2, "Device down", AlertRuleType.StateChange, Severity.Critical, new RuleScope(), StateKeys.DeviceDown, null, null, null, TimeSpan.Zero);
        var device = Device();

        var raise = Assert.Single(evaluator.Evaluate(new StateSignal(1, null, StateKeys.DeviceDown, null, true, "down", T0), device, [rule]));
        Assert.Equal(TransitionKind.Raise, raise.Kind);
        Assert.Equal("Device down: SW-1", raise.Title);
        Assert.Empty(evaluator.Evaluate(new StateSignal(1, null, StateKeys.DeviceDown, null, true, "down", T0.AddMinutes(1)), device, [rule]));
        Assert.Equal(TransitionKind.Resolve, Assert.Single(evaluator.Evaluate(new StateSignal(1, null, StateKeys.DeviceDown, null, false, "up", T0.AddMinutes(2)), device, [rule])).Kind);
    }

    [Fact]
    public void StateChange_ImportantInterfacesOnly()
    {
        var evaluator = new AlertEvaluator();
        var rule = new AlertRuleDefinition(3, "Port down", AlertRuleType.StateChange, Severity.Major, new RuleScope { ImportantInterfacesOnly = true },
            StateKeys.InterfaceDown, null, null, null, TimeSpan.Zero);
        var device = Device();

        Assert.Empty(evaluator.Evaluate(new StateSignal(1, 10, StateKeys.InterfaceDown, null, true, "down", T0, "Gi1/0/1", IsImportant: false), device, [rule]));
        var raised = Assert.Single(evaluator.Evaluate(new StateSignal(1, 11, StateKeys.InterfaceDown, null, true, "down", T0, "Te1/1/1", IsImportant: true), device, [rule]));
        Assert.Equal(11, raised.InterfaceId);
        Assert.Equal("Port down: SW-1 Te1/1/1", raised.Title);

        // Port un-marked as important while down: the alert resolves.
        Assert.Equal(TransitionKind.Resolve, Assert.Single(evaluator.Evaluate(new StateSignal(1, 11, StateKeys.InterfaceDown, null, true, "down", T0.AddMinutes(1), "Te1/1/1", IsImportant: false), device, [rule])).Kind);
    }

    [Fact]
    public void Scope_FiltersByTypeGroupAndTag()
    {
        var evaluator = new AlertEvaluator();
        var scoped = CpuRule(durationSec: 0) with
        {
            Scope = new RuleScope { DeviceTypes = [DeviceType.Server], GroupIds = [7], Tags = ["CORE"] },
        };

        Assert.Empty(evaluator.Evaluate(Cpu(99, 0), Device(type: DeviceType.Switch, groups: 7), [scoped]));
        Assert.Empty(evaluator.Evaluate(Cpu(99, 0), Device(type: DeviceType.Server, groups: 3), [scoped]));
        Assert.Single(evaluator.Evaluate(Cpu(99, 0, deviceId: 4), Device(4, type: DeviceType.Server, groups: [3, 7]), [scoped]));
    }

    [Fact]
    public void EventMatch_RaisesEveryTimeWithSameDedupKey()
    {
        var evaluator = new AlertEvaluator();
        var rule = new AlertRuleDefinition(4, "Rebooted", AlertRuleType.EventMatch, Severity.Warning, new RuleScope(), EventKeys.DeviceReboot, null, null, null, TimeSpan.Zero);
        var a = Assert.Single(evaluator.Evaluate(new EventSignal(1, EventKeys.DeviceReboot, "rebooted", T0), Device(), [rule]));
        var b = Assert.Single(evaluator.Evaluate(new EventSignal(1, EventKeys.DeviceReboot, "rebooted", T0.AddHours(1)), Device(), [rule]));
        Assert.Equal(a.DedupKey, b.DedupKey);
        Assert.False(a.AutoResolve);
    }

    [Fact]
    public void MarkRaised_AllowsResolveAfterRestart()
    {
        var evaluator = new AlertEvaluator();
        var rule = CpuRule(durationSec: 0);
        var key = AlertEvaluator.DedupKey(rule.Id, AlertEvaluator.EntityKey(1, null, null));
        evaluator.MarkRaised(key);

        var resolved = Assert.Single(evaluator.Evaluate(Cpu(10, 0), Device(), [rule]));
        Assert.Equal(TransitionKind.Resolve, resolved.Kind);
        Assert.Equal(key, resolved.DedupKey);
    }

    [Fact]
    public void Reset_AllowsRaisingAgain()
    {
        var evaluator = new AlertEvaluator();
        var rule = CpuRule(durationSec: 0);
        var raised = Assert.Single(evaluator.Evaluate(Cpu(99, 0), Device(), [rule]));
        evaluator.Reset(raised.DedupKey);
        Assert.Single(evaluator.Evaluate(Cpu(99, 1), Device(), [rule]));
    }

    [Theory]
    [InlineData("r12|1:-:-", true, 12)]
    [InlineData("r7|3:44:-", true, 7)]
    [InlineData("x7|3", false, 0)]
    [InlineData("r|3", false, 0)]
    public void TryParseRuleId(string key, bool ok, int expected)
    {
        Assert.Equal(ok, AlertEvaluator.TryParseRuleId(key, out var id));
        Assert.Equal(expected, id);
    }

    [Fact]
    public void RuleScope_CorruptJsonMatchesNothing()
    {
        var scope = RuleScope.Parse("{not json");
        Assert.False(scope.Matches(Device(), false, false));
        Assert.True(RuleScope.Parse("").Matches(Device(), false, false));
    }

    [Fact]
    public void RuleScope_RoundTripsJson()
    {
        var scope = new RuleScope { DeviceTypes = [DeviceType.VM], Tags = ["erp"], ImportantInterfacesOnly = true };
        var parsed = RuleScope.Parse(scope.ToJson());
        Assert.Equal([DeviceType.VM], parsed.DeviceTypes);
        Assert.Equal(["erp"], parsed.Tags);
        Assert.True(parsed.ImportantInterfacesOnly);
    }
}
