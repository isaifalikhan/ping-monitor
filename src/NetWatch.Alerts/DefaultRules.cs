using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;

namespace NetWatch.Alerts;

/// <summary>
/// Built-in rules seeded on first start (prompt §10 default thresholds). They are editable and can be disabled,
/// but not deleted.
/// </summary>
public static class DefaultRules
{
    public static IReadOnlyList<AlertRule> Create(DateTime nowUtc)
    {
        var important = new RuleScope { ImportantInterfacesOnly = true }.ToJson();
        var all = new RuleScope().ToJson();

        return
        [
            State("Device down", "Device is not reachable (ping and SNMP) for 3 consecutive polls.", StateKeys.DeviceDown, Severity.Critical, 0, all),
            Threshold("High ping latency", MetricKeys.PingRtt, ComparisonOperator.GreaterThan, 100, 80, 300, Severity.Warning, all),
            Threshold("Ping packet loss", MetricKeys.PingLoss, ComparisonOperator.GreaterThan, 20, 10, 180, Severity.Warning, all),
            Threshold("High CPU", MetricKeys.Cpu, ComparisonOperator.GreaterThan, 90, 85, 300, Severity.Major, all),
            Threshold("High memory", MetricKeys.Memory, ComparisonOperator.GreaterThan, 90, 85, 300, Severity.Major, all),
            State("Important port down", "An interface flagged as important (uplink/server port) is down while admin-up.", StateKeys.InterfaceDown, Severity.Major, 0, important),
            Threshold("High interface utilization", MetricKeys.InterfaceUtilization, ComparisonOperator.GreaterThan, 90, 80, 300, Severity.Warning, important),
            State("Hardware sensor problem", "A fan, power supply or temperature sensor reports a warning or failure.", StateKeys.SensorProblem, Severity.Major, 0, all),
            State("SNMP not responding", "Device answers ping but not SNMP (check credentials/ACL).", StateKeys.SnmpUnreachable, Severity.Warning, 180, all),
            Event("Device rebooted", "sysUpTime went backwards: the device restarted.", EventKeys.DeviceReboot, Severity.Warning, all),
        ];

        AlertRule Threshold(string name, string key, ComparisonOperator op, double threshold, double clear, int durationSec, Severity severity, string scope) => new()
        {
            Name = name,
            Description = $"{MetricKeys.DisplayNames.GetValueOrDefault(key, key)} above {threshold} for {durationSec / 60} min.",
            IsBuiltIn = true,
            IsEnabled = true,
            RuleType = AlertRuleType.Threshold,
            Key = key,
            Operator = op,
            Threshold = threshold,
            ClearThreshold = clear,
            DurationSec = durationSec,
            Severity = severity,
            ScopeJson = scope,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };

        AlertRule State(string name, string description, string key, Severity severity, int durationSec, string scope) => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsEnabled = true,
            RuleType = AlertRuleType.StateChange,
            Key = key,
            DurationSec = durationSec,
            Severity = severity,
            ScopeJson = scope,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };

        AlertRule Event(string name, string description, string key, Severity severity, string scope) => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsEnabled = true,
            RuleType = AlertRuleType.EventMatch,
            Key = key,
            Severity = severity,
            ScopeJson = scope,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };
    }
}
