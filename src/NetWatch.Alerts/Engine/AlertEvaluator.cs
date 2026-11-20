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
    TimeSpan Duration,
    EventFilter? Filter = null,
    TimeSpan Window = default,
    int CountThreshold = 0,
    int? EscalateAfterMin = null)
{
    public EventFilter EffectiveFilter => Filter ?? EmptyFilter;

    private static readonly EventFilter EmptyFilter = new();

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
        TimeSpan.FromSeconds(Math.Max(0, rule.DurationSec)),
        EventFilter.Parse(rule.FilterJson),
        TimeSpan.FromSeconds(Math.Max(0, rule.WindowSec)),
        Math.Max(0, rule.CountThreshold),
        rule.EscalateAfterMin);

    /// <summary>Compares everything that affects evaluation (used to decide whether timing state must be reset).</summary>
    public bool SameCondition(AlertRuleDefinition other) =>
        Type == other.Type && Key == other.Key && Operator == other.Operator && Threshold == other.Threshold &&
        ClearThreshold == other.ClearThreshold && Duration == other.Duration && Window == other.Window &&
        CountThreshold == other.CountThreshold && Scope.ToJson() == other.Scope.ToJson() &&
        EffectiveFilter.ToJson() == other.EffectiveFilter.ToJson();
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
/// "for N minutes" rules work, whether an alert is currently raised, event counts for count-in-window rules and
/// the last data time for absence rules. Not thread-safe: callers serialise access.
/// </summary>
/// <remarks>
/// Suppression (maintenance or an unreachable upstream device) only prevents <em>new</em> alerts: the condition
/// keeps its timing, so if it still holds after the suppression ends the alert is raised at the next signal.
/// </remarks>
public sealed class AlertEvaluator
{
    private readonly Dictionary<string, EntityState> _states = new(StringComparer.Ordinal);

    private sealed class EntityState
    {
        public int RuleId;
        public int DeviceId;
        public int? InterfaceId;
        public string? EntityName;
        public DateTime? ConditionSince;
        public bool Raised;

        /// <summary>Why the last raise attempt was suppressed (null when not suppressed).</summary>
        public string? SuppressedBy;

        /// <summary>Absence: last time data arrived.</summary>
        public DateTime? LastSeen;

        /// <summary>CountInWindow: timestamps of matching events inside the window.</summary>
        public Queue<DateTime>? Events;
        public DateTime LastRefresh;
        public string? LastMessage;
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

    /// <summary>Conditions that currently hold but whose alert is suppressed, with the reason.</summary>
    public IReadOnlyList<(int DeviceId, int RuleId, string Reason)> Suppressed =>
        _states.Values.Where(s => s.SuppressedBy is not null && !s.Raised).Select(s => (s.DeviceId, s.RuleId, s.SuppressedBy!)).ToList();

    /// <summary>Marks an alert as raised (used at start-up for alerts already open in the database).</summary>
    public void MarkRaised(string dedupKey) => GetState(dedupKey, 0, 0, null, null).Raised = true;

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

    /// <param name="suppressedBy">Name of an unreachable upstream device, or null. Maintenance comes from <paramref name="device"/>.</param>
    public IReadOnlyList<AlertTransition> Evaluate(AlertSignal signal, DeviceContext device, IReadOnlyList<AlertRuleDefinition> rules, string? suppressedBy = null)
    {
        var suppression = device.InMaintenance ? "maintenance" : suppressedBy is null ? null : $"upstream device {suppressedBy} unreachable";
        List<AlertTransition>? result = null;
        foreach (var rule in rules)
        {
            var transition = (signal, rule.Type) switch
            {
                (MetricSignal m, AlertRuleType.Threshold) when rule.Key == m.MetricKey => EvaluateThreshold(rule, m, device, suppression),
                (MetricSignal m, AlertRuleType.Absence) when rule.Key == m.MetricKey => DataSeen(rule, m.DeviceId, m.InterfaceId, m.Instance, m.EntityName, device, m.TimestampUtc, m.IsImportant),
                (StateSignal s, AlertRuleType.StateChange) when rule.Key == s.StateKey => EvaluateState(rule, s, device, suppression),
                (EventSignal e, AlertRuleType.EventMatch) when rule.Key == e.EventKey => EvaluateEvent(rule, e, device, suppression),
                (EventSignal e, AlertRuleType.CountInWindow) when rule.Key == e.EventKey => EvaluateCount(rule, e, device, suppression),
                (EventSignal e, AlertRuleType.Absence) when rule.Key == e.EventKey && rule.EffectiveFilter.Matches(e) =>
                    DataSeen(rule, e.DeviceId, null, null, null, device, e.TimestampUtc, false),
                _ => null,
            };

            if (transition is not null)
            {
                (result ??= new List<AlertTransition>()).Add(transition);
            }
        }

        return (IReadOnlyList<AlertTransition>?)result ?? Array.Empty<AlertTransition>();
    }

    /// <summary>
    /// Time-driven evaluation (called periodically): raises absence alerts and resolves count-in-window alerts whose
    /// window has gone quiet. Also drops idle state.
    /// </summary>
    public IReadOnlyList<AlertTransition> Tick(
        DateTime nowUtc,
        Func<int, DeviceContext?> devices,
        IReadOnlyList<AlertRuleDefinition> rules,
        Func<int, string?> upstreamUnreachable)
    {
        var byId = rules.ToDictionary(r => r.Id);
        var result = new List<AlertTransition>();
        var remove = new List<string>();

        foreach (var (key, state) in _states)
        {
            if (!byId.TryGetValue(state.RuleId, out var rule))
            {
                continue;
            }

            var device = devices(state.DeviceId);
            if (device is null)
            {
                remove.Add(key);
                continue;
            }

            if (rule.Type == AlertRuleType.CountInWindow && state.Events is { } events)
            {
                Prune(events, nowUtc - rule.Window);
                if (state.Raised && events.Count == 0)
                {
                    state.Raised = false;
                    result.Add(new AlertTransition(TransitionKind.Resolve, rule, key, state.DeviceId, null, BuildTitle(rule, device, null),
                        $"No matching events for {Span(rule.Window)}.", 0, nowUtc, AutoResolve: true));
                }
                else if (!state.Raised && events.Count == 0)
                {
                    remove.Add(key);
                }
            }
            else if (rule.Type == AlertRuleType.Absence && state.LastSeen is { } lastSeen && !state.Raised && device.IsEnabled &&
                     nowUtc - lastSeen >= rule.Duration && rule.Duration > TimeSpan.Zero)
            {
                var suppressedBy = device.InMaintenance ? "maintenance" : upstreamUnreachable(state.DeviceId) is { } up ? $"upstream device {up} unreachable" : null;
                if (suppressedBy is not null)
                {
                    state.SuppressedBy = suppressedBy;
                    continue;
                }

                state.Raised = true;
                state.SuppressedBy = null;
                var name = KeyName(rule.Key);
                var message = string.Create(CultureInfo.InvariantCulture,
                    $"No {name} data for {Span(rule.Duration)} (last received {(nowUtc - lastSeen).TotalMinutes:0} min ago).");
                result.Add(new AlertTransition(TransitionKind.Raise, rule, key, state.DeviceId, state.InterfaceId,
                    BuildTitle(rule, device, state.EntityName), message, null, nowUtc, AutoResolve: true));
            }
        }

        foreach (var key in remove)
        {
            _states.Remove(key);
        }

        return result;
    }

    // ------------------------------------------------------------------ threshold

    private AlertTransition? EvaluateThreshold(AlertRuleDefinition rule, MetricSignal m, DeviceContext device, string? suppression)
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
        var state = GetState(dedup, rule.Id, m.DeviceId, m.InterfaceId, m.EntityName);
        var breached = Compare(m.Value, op, rule.Threshold.Value);

        if (breached)
        {
            state.ConditionSince ??= m.TimestampUtc;
            if (!state.Raised && m.TimestampUtc - state.ConditionSince.Value >= rule.Duration)
            {
                if (suppression is not null)
                {
                    state.SuppressedBy = suppression;
                    return null;
                }

                state.Raised = true;
                state.SuppressedBy = null;
                var metricName = KeyName(rule.Key);
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
            state.SuppressedBy = null;
            return null;
        }

        if (cleared)
        {
            return ResolveIfRaised(rule, dedup, m.DeviceId, m.InterfaceId, device, m.EntityName, m.Value, m.TimestampUtc,
                string.Create(CultureInfo.InvariantCulture, $"{KeyName(rule.Key)} back to {m.Value:0.##}."));
        }

        return null;
    }

    // ------------------------------------------------------------------ state

    private AlertTransition? EvaluateState(AlertRuleDefinition rule, StateSignal s, DeviceContext device, string? suppression)
    {
        var dedup = DedupKey(rule.Id, EntityKey(s.DeviceId, s.InterfaceId, s.Instance));
        if (!rule.Scope.Matches(device, s.IsImportant, s.InterfaceId.HasValue))
        {
            return ResolveIfRaised(rule, dedup, s.DeviceId, s.InterfaceId, device, s.EntityName, null, s.TimestampUtc, "No longer in rule scope");
        }

        var state = GetState(dedup, rule.Id, s.DeviceId, s.InterfaceId, s.EntityName);
        if (s.IsProblem)
        {
            state.ConditionSince ??= s.TimestampUtc;
            if (!state.Raised && s.TimestampUtc - state.ConditionSince.Value >= rule.Duration)
            {
                if (suppression is not null)
                {
                    state.SuppressedBy = suppression;
                    return null;
                }

                state.Raised = true;
                state.SuppressedBy = null;
                return Raise(rule, dedup, s.DeviceId, s.InterfaceId, device, s.EntityName, s.Description, null, s.TimestampUtc, autoResolve: true);
            }

            return null;
        }

        state.ConditionSince = null;
        state.SuppressedBy = null;
        return ResolveIfRaised(rule, dedup, s.DeviceId, s.InterfaceId, device, s.EntityName, null, s.TimestampUtc, s.Description);
    }

    // ------------------------------------------------------------------ events

    private static AlertTransition? EvaluateEvent(AlertRuleDefinition rule, EventSignal e, DeviceContext device, string? suppression)
    {
        if (suppression is not null || !rule.Scope.Matches(device, interfaceIsImportant: false, isInterfaceSignal: false) || !rule.EffectiveFilter.Matches(e))
        {
            return null;
        }

        var dedup = DedupKey(rule.Id, EntityKey(e.DeviceId, null, null));

        // Event alerts do not clear by themselves: an operator acknowledges and closes them.
        return Raise(rule, dedup, e.DeviceId, null, device, null, Truncate(e.Message), null, e.TimestampUtc, autoResolve: false);
    }

    private AlertTransition? EvaluateCount(AlertRuleDefinition rule, EventSignal e, DeviceContext device, string? suppression)
    {
        if (rule.CountThreshold <= 0 || rule.Window <= TimeSpan.Zero ||
            !rule.Scope.Matches(device, interfaceIsImportant: false, isInterfaceSignal: false) || !rule.EffectiveFilter.Matches(e))
        {
            return null;
        }

        var dedup = DedupKey(rule.Id, EntityKey(e.DeviceId, null, null));
        var state = GetState(dedup, rule.Id, e.DeviceId, null, null);
        var events = state.Events ??= new Queue<DateTime>();
        events.Enqueue(e.TimestampUtc);
        Prune(events, e.TimestampUtc - rule.Window);

        // Bound memory for floods: only the count matters once the threshold is far exceeded.
        while (events.Count > Math.Max(rule.CountThreshold * 4, 1000))
        {
            events.Dequeue();
        }

        state.LastMessage = e.Message;
        if (events.Count < rule.CountThreshold)
        {
            return null;
        }

        var message = string.Create(CultureInfo.InvariantCulture,
            $"{events.Count} matching {KeyName(rule.Key).ToLowerInvariant()} events in {Span(rule.Window)}. Last: {Truncate(e.Message, 300)}");

        if (state.Raised)
        {
            // Refresh the open alert (count, last message) at most once a minute.
            if (e.TimestampUtc - state.LastRefresh < TimeSpan.FromMinutes(1))
            {
                return null;
            }

            state.LastRefresh = e.TimestampUtc;
            return Raise(rule, dedup, e.DeviceId, null, device, null, message, events.Count, e.TimestampUtc, autoResolve: true);
        }

        if (suppression is not null)
        {
            state.SuppressedBy = suppression;
            return null;
        }

        state.Raised = true;
        state.SuppressedBy = null;
        state.LastRefresh = e.TimestampUtc;
        return Raise(rule, dedup, e.DeviceId, null, device, null, message, events.Count, e.TimestampUtc, autoResolve: true);
    }

    // ------------------------------------------------------------------ absence

    private AlertTransition? DataSeen(AlertRuleDefinition rule, int deviceId, int? interfaceId, string? instance, string? entityName, DeviceContext device, DateTime ts, bool important)
    {
        if (!rule.Scope.Matches(device, important, interfaceId.HasValue))
        {
            return null;
        }

        var dedup = DedupKey(rule.Id, EntityKey(deviceId, interfaceId, instance));
        var state = GetState(dedup, rule.Id, deviceId, interfaceId, entityName);
        state.LastSeen = ts;
        state.SuppressedBy = null;
        return state.Raised
            ? ResolveIfRaised(rule, dedup, deviceId, interfaceId, device, entityName, null, ts, $"{KeyName(rule.Key)} data is arriving again.")
            : null;
    }

    // ------------------------------------------------------------------ helpers

    private AlertTransition? ResolveIfRaised(AlertRuleDefinition rule, string dedup, int deviceId, int? interfaceId, DeviceContext device, string? entityName, double? value, DateTime ts, string message)
    {
        if (!_states.TryGetValue(dedup, out var state))
        {
            return null;
        }

        state.ConditionSince = null;
        state.SuppressedBy = null;
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

    private EntityState GetState(string dedup, int ruleId, int deviceId, int? interfaceId, string? entityName)
    {
        if (!_states.TryGetValue(dedup, out var state))
        {
            state = new EntityState();
            _states[dedup] = state;
        }

        // States created by MarkRaised at start-up learn their identity from the first real signal.
        if (ruleId != 0)
        {
            state.RuleId = ruleId;
            state.DeviceId = deviceId;
            state.InterfaceId = interfaceId;
            state.EntityName = entityName ?? state.EntityName;
        }

        return state;
    }

    private static void Prune(Queue<DateTime> events, DateTime cutoff)
    {
        while (events.Count > 0 && events.Peek() <= cutoff)
        {
            events.Dequeue();
        }
    }

    private static string KeyName(string key) =>
        MetricKeys.DisplayNames.GetValueOrDefault(key) ?? EventKeys.DisplayNames.GetValueOrDefault(key) ?? StateKeys.DisplayNames.GetValueOrDefault(key) ?? key;

    private static string Truncate(string text, int max = 1000) => text.Length <= max ? text : text[..max] + "…";

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

    private static string Span(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMinutes:0.#} min")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0} s");

    private static string DurationText(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? string.Empty
        : duration.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $" for {duration.TotalMinutes:0.#} min")
        : string.Create(CultureInfo.InvariantCulture, $" for {duration.TotalSeconds:0} s");
}
