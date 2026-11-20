using System.Globalization;
using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;

namespace NetWatch.Alerts.Engine;

/// <summary>Immutable, parsed form of an <see cref="AlertRule"/> used by the evaluator.</summary>
public sealed record AlertRuleDefinition(
    int Id,
    string Name,
    AlertRuleType Type,
    Severity Severity,
    RuleScope Scope,
    string Key,
    ComparisonOperator? Operator,
    double? Threshold,
    double? ClearThreshold,
    TimeSpan Duration)
{
    public static AlertRuleDefinition From(AlertRule rule) => new(
        rule.Id,
        rule.Name,
        rule.RuleType,
        rule.Severity,
        RuleScope.Parse(rule.ScopeJson),
        rule.Key,
        rule.Operator,
        rule.Threshold,
        rule.ClearThreshold,
        TimeSpan.FromSeconds(Math.Max(0, rule.DurationSec)));
}

public enum TransitionKind
{
    Raise,
    Resolve
}

/// <summary>Output of the evaluator: an alert must be raised (or refreshed) or resolved.</summary>
public sealed record AlertTransition(
    TransitionKind Kind,
    AlertRuleDefinition Rule,
    string DedupKey,
    int DeviceId,
    int? InterfaceId,
    string Title,
    string Message,
    double? Value,
    DateTime TimestampUtc,
    bool AutoResolve);

/// <summary>
/// Stateful but I/O-free rule evaluator. Tracks, per rule and entity, since when a condition has held so that
/// "for N minutes" rules work, and whether an alert is currently raised. Not thread-safe: callers serialise access.
/// </summary>
public sealed class AlertEvaluator
{
    private readonly Dictionary<string, EntityState> _states = new(StringComparer.Ordinal);

    private sealed class EntityState
    {
        public DateTime? ConditionSince;
        public bool Raised;
    }

    public static string EntityKey(int deviceId, int? interfaceId, string? instance) =>
        string.Create(CultureInfo.InvariantCulture, $"{deviceId}:{(interfaceId?.ToString(CultureInfo.InvariantCulture) ?? "-")}:{instance ?? "-"}");

    public static string DedupKey(int ruleId, string entityKey) =>
        string.Create(CultureInfo.InvariantCulture, $"r{ruleId}|{entityKey}");

    public static bool TryParseRuleId(string dedupKey, out int ruleId)
    {
        ruleId = 0;
        var bar = dedupKey.IndexOf('|');
        return dedupKey.StartsWith('r') && bar > 1 &&
               int.TryParse(dedupKey.AsSpan(1, bar - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out ruleId);
    }

    /// <summary>Number of tracked rule/entity states (self-monitoring).</summary>
    public int TrackedStates => _states.Count;

    /// <summary>Marks an alert as raised (used at start-up for alerts already open in the database).</summary>
    public void MarkRaised(string dedupKey) => GetState(dedupKey).Raised = true;

    /// <summary>Forgets the state of one alert (after it was closed manually, so it can be raised again).</summary>
    public void Reset(string dedupKey) => _states.Remove(dedupKey);

    /// <summary>Forgets all state of a rule (rule deleted, disabled or edited).</summary>
    public void ResetRule(int ruleId)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"r{ruleId}|");
        foreach (var key in _states.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _states.Remove(key);
        }
    }

    public IReadOnlyList<AlertTransition> Evaluate(AlertSignal signal, DeviceContext device, IReadOnlyList<AlertRuleDefinition> rules)
    {
        List<AlertTransition>? result = null;
        foreach (var rule in rules)
        {
            var transition = signal switch
            {
                MetricSignal m when rule.Type == AlertRuleType.Threshold && rule.Key == m.MetricKey => EvaluateThreshold(rule, m, device),
                StateSignal s when rule.Type == AlertRuleType.StateChange && rule.Key == s.StateKey => EvaluateState(rule, s, device),
                EventSignal e when rule.Type == AlertRuleType.EventMatch && rule.Key == e.EventKey => EvaluateEvent(rule, e, device),
                _ => null,
            };

            if (transition is not null)
            {
                (result ??= new List<AlertTransition>()).Add(transition);
            }
        }

        return (IReadOnlyList<AlertTransition>?)result ?? Array.Empty<AlertTransition>();
    }

    private AlertTransition? EvaluateThreshold(AlertRuleDefinition rule, MetricSignal m, DeviceContext device)
    {
        if (rule.Operator is null || rule.Threshold is null)
        {
            return null;
        }

        var dedup = DedupKey(rule.Id, EntityKey(m.DeviceId, m.InterfaceId, m.Instance));
        if (!rule.Scope.Matches(device, m.IsImportant, m.InterfaceId.HasValue))
        {
            return ResolveIfRaised(rule, dedup, m.DeviceId, m.InterfaceId, device, m.EntityName, m.Value, m.TimestampUtc, "No longer in rule scope");
        }

        var op = rule.Operator.Value;
        var state = GetState(dedup);
        var breached = Compare(m.Value, op, rule.Threshold.Value);

        if (breached)
        {
            state.ConditionSince ??= m.TimestampUtc;
            if (!state.Raised && !device.InMaintenance && m.TimestampUtc - state.ConditionSince.Value >= rule.Duration)
            {
                state.Raised = true;
                var metricName = MetricKeys.DisplayNames.GetValueOrDefault(rule.Key, rule.Key);
                var message = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{metricName} is {m.Value:0.##} ({OperatorText(op)} {rule.Threshold.Value:0.##}){DurationText(rule.Duration)}.");
                return Raise(rule, dedup, m.DeviceId, m.InterfaceId, device, m.EntityName, message, m.Value, m.TimestampUtc, autoResolve: true);
            }

            return null;
        }

        // Hysteresis: once raised, the value must pass the clear threshold (if any) before resolving.
        var cleared = !Compare(m.Value, op, rule.ClearThreshold ?? rule.Threshold.Value);
        if (!state.Raised)
        {
            state.ConditionSince = null;
            return null;
        }

        if (cleared)
        {
            var metricName = MetricKeys.DisplayNames.GetValueOrDefault(rule.Key, rule.Key);
            return ResolveIfRaised(rule, dedup, m.DeviceId, m.InterfaceId, device, m.EntityName, m.Value, m.TimestampUtc,
                string.Create(CultureInfo.InvariantCulture, $"{metricName} back to {m.Value:0.##}."));
        }

        return null;
    }

    private AlertTransition? EvaluateState(AlertRuleDefinition rule, StateSignal s, DeviceContext device)
    {
        var dedup = DedupKey(rule.Id, EntityKey(s.DeviceId, s.InterfaceId, s.Instance));
        if (!rule.Scope.Matches(device, s.IsImportant, s.InterfaceId.HasValue))
        {
            return ResolveIfRaised(rule, dedup, s.DeviceId, s.InterfaceId, device, s.EntityName, null, s.TimestampUtc, "No longer in rule scope");
        }

        var state = GetState(dedup);
        if (s.IsProblem)
        {
            state.ConditionSince ??= s.TimestampUtc;
            if (!state.Raised && !device.InMaintenance && s.TimestampUtc - state.ConditionSince.Value >= rule.Duration)
            {
                state.Raised = true;
                return Raise(rule, dedup, s.DeviceId, s.InterfaceId, device, s.EntityName, s.Description, null, s.TimestampUtc, autoResolve: true);
            }

            return null;
        }

        state.ConditionSince = null;
        return ResolveIfRaised(rule, dedup, s.DeviceId, s.InterfaceId, device, s.EntityName, null, s.TimestampUtc, s.Description);
    }

    private static AlertTransition? EvaluateEvent(AlertRuleDefinition rule, EventSignal e, DeviceContext device)
    {
        if (device.InMaintenance || !rule.Scope.Matches(device, interfaceIsImportant: false, isInterfaceSignal: false))
        {
            return null;
        }

        var dedup = DedupKey(rule.Id, EntityKey(e.DeviceId, null, null));

        // Event alerts do not clear by themselves: an operator acknowledges and closes them.
        return Raise(rule, dedup, e.DeviceId, null, device, null, e.Message, null, e.TimestampUtc, autoResolve: false);
    }

    private AlertTransition? ResolveIfRaised(AlertRuleDefinition rule, string dedup, int deviceId, int? interfaceId, DeviceContext device, string? entityName, double? value, DateTime ts, string message)
    {
        if (!_states.TryGetValue(dedup, out var state))
        {
            return null;
        }

        state.ConditionSince = null;
        if (!state.Raised)
        {
            return null;
        }

        state.Raised = false;
        return new AlertTransition(TransitionKind.Resolve, rule, dedup, deviceId, interfaceId, BuildTitle(rule, device, entityName), message, value, ts, AutoResolve: true);
    }

    private static AlertTransition Raise(AlertRuleDefinition rule, string dedup, int deviceId, int? interfaceId, DeviceContext device, string? entityName, string message, double? value, DateTime ts, bool autoResolve) =>
        new(TransitionKind.Raise, rule, dedup, deviceId, interfaceId, BuildTitle(rule, device, entityName), message, value, ts, autoResolve);

    private static string BuildTitle(AlertRuleDefinition rule, DeviceContext device, string? entityName) =>
        entityName is null ? $"{rule.Name}: {device.Name}" : $"{rule.Name}: {device.Name} {entityName}";

    private EntityState GetState(string dedup)
    {
        if (!_states.TryGetValue(dedup, out var state))
        {
            state = new EntityState();
            _states[dedup] = state;
        }

        return state;
    }

    public static bool Compare(double value, ComparisonOperator op, double threshold) => op switch
    {
        ComparisonOperator.GreaterThan => value > threshold,
        ComparisonOperator.GreaterOrEqual => value >= threshold,
        ComparisonOperator.LessThan => value < threshold,
        ComparisonOperator.LessOrEqual => value <= threshold,
        ComparisonOperator.Equal => Math.Abs(value - threshold) < 1e-9,
        ComparisonOperator.NotEqual => Math.Abs(value - threshold) >= 1e-9,
        _ => false,
    };

    private static string OperatorText(ComparisonOperator op) => op switch
    {
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterOrEqual => ">=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessOrEqual => "<=",
        ComparisonOperator.Equal => "=",
        _ => "!=",
    };

    private static string DurationText(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? string.Empty
        : duration.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $" for {duration.TotalMinutes:0.#} min")
        : string.Create(CultureInfo.InvariantCulture, $" for {duration.TotalSeconds:0} s");
}
