using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;

namespace NetWatch.Alerts;

/// <summary>
/// Built-in rules seeded on first start and added on upgrade when missing (prompt §10 default thresholds). They
/// are editable and can be disabled, but not deleted.
/// </summary>
public static class DefaultRules
{
    public static IReadOnlyList<AlertRule> Create(DateTime nowUtc)
    {
        var important = new RuleScope { ImportantInterfacesOnly = true }.ToJson();
        var all = new RuleScope().ToJson();

        return
        [
            // ---- Phase 1: availability, switches, health
            State("Device down", "Device is not reachable (ping and SNMP) for 3 consecutive polls.", StateKeys.DeviceDown, Severity.Critical, 0, all),
            Threshold("High ping latency", MetricKeys.PingRtt, ComparisonOperator.GreaterThan, 100, 80, 300, Severity.Warning, all),
            Threshold("Ping packet loss", MetricKeys.PingLoss, ComparisonOperator.GreaterThan, 20, 10, 180, Severity.Warning, all),
            Threshold("High CPU", MetricKeys.Cpu, ComparisonOperator.GreaterThan, 90, 85, 300, Severity.Major, all),
            Threshold("High memory", MetricKeys.Memory, ComparisonOperator.GreaterThan, 90, 85, 300, Severity.Major, all),
            State("Important port down", "An interface flagged as important (uplink/server port) is down while admin-up.", StateKeys.InterfaceDown, Severity.Major, 0, important),
            Threshold("High interface utilization", MetricKeys.InterfaceUtilization, ComparisonOperator.GreaterThan, 90, 80, 300, Severity.Warning, important),
            State("Hardware sensor problem", "A fan, power supply or temperature sensor reports a warning or failure.", StateKeys.SensorProblem, Severity.Major, 0, all),
            State("SNMP not responding", "Device answers ping but not SNMP (check credentials/ACL).", StateKeys.SnmpUnreachable, Severity.Warning, 180, all),
            Event("Device rebooted", "sysUpTime went backwards: the device restarted.", EventKeys.DeviceReboot, Severity.Warning, all, null),

            // ---- Phase 2: servers
            Threshold("Disk space low", MetricKeys.DiskUsed, ComparisonOperator.GreaterThan, 85, 80, 120, Severity.Warning, all),
            Threshold("Disk space critical", MetricKeys.DiskUsed, ComparisonOperator.GreaterThan, 95, 92, 120, Severity.Critical, all),
            State("Service not running", "A watched Windows service or systemd unit is not running.", StateKeys.ServiceDown, Severity.Major, 60, all),
            State("Server monitoring failing", "The server answers ping but WinRM/WMI or SSH collection fails (credentials, firewall, permissions).", StateKeys.ServerCollectionFailed, Severity.Warning, 300, all),
            Threshold("High load (Linux)", MetricKeys.LoadPerCpu, ComparisonOperator.GreaterThan, 2, 1.5, 600, Severity.Warning, all),
            Event("Windows critical event", "A Critical-level entry was written to the System or Application event log.", EventKeys.WindowsEvent, Severity.Major, all,
                new EventFilter { MinSeverity = Severity.Critical }),

            // ---- Phase 2: syslog and traps
            Event("Syslog emergency/alert/critical", "A device sent a syslog message with severity 0–2 (emergency, alert, critical).", EventKeys.SyslogMessage, Severity.Major, all,
                new EventFilter { MinSeverity = Severity.Critical }),
            Event("SNMP authentication failure", "A device reported an SNMP request with a wrong community or v3 credentials (authenticationFailure trap).", EventKeys.SnmpTrap, Severity.Warning, all,
                new EventFilter { Oid = "1.3.6.1.6.3.1.1.5.5" }),
            Count("Repeated login failures (syslog)", "At least 10 syslog messages about failed logins from one device within 5 minutes (possible brute force).",
                EventKeys.SyslogMessage, 10, 300, Severity.Warning, all,
                new EventFilter { Pattern = @"authentication fail|failed password|login fail|invalid user|bad password|failed login" }),
        ];

        AlertRule Threshold(string name, string key, ComparisonOperator op, double threshold, double clear, int durationSec, Severity severity, string scope) => new()
        {
            Name = name,
            Description = durationSec > 0
                ? $"{MetricKeys.DisplayNames.GetValueOrDefault(key, key)} above {threshold} for {durationSec / 60} min."
                : $"{MetricKeys.DisplayNames.GetValueOrDefault(key, key)} above {threshold}.",
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

        AlertRule Event(string name, string description, string key, Severity severity, string scope, EventFilter? filter) => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsEnabled = true,
            RuleType = AlertRuleType.EventMatch,
            Key = key,
            FilterJson = filter?.ToJson(),
            Severity = severity,
            ScopeJson = scope,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };

        AlertRule Count(string name, string description, string key, int count, int windowSec, Severity severity, string scope, EventFilter filter) => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsEnabled = true,
            RuleType = AlertRuleType.CountInWindow,
            Key = key,
            FilterJson = filter.ToJson(),
            CountThreshold = count,
            WindowSec = windowSec,
            Severity = severity,
            ScopeJson = scope,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };
    }
}
