namespace NetWatch.Core.Entities;

public class AlertRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    public AlertRuleType RuleType { get; set; }
    public Severity Severity { get; set; } = Severity.Warning;

    /// <summary>JSON-serialised <see cref="Alerts.RuleScope"/>. Empty scope = all devices.</summary>
    public string ScopeJson { get; set; } = "{}";

    /// <summary>Metric key (Threshold), state key (StateChange) or event key (EventMatch). See <see cref="Monitoring.MetricKeys"/>.</summary>
    public string Key { get; set; } = string.Empty;
    public ComparisonOperator? Operator { get; set; }
    public double? Threshold { get; set; }

    /// <summary>Optional hysteresis: the alert only clears once the value passes this level.</summary>
    public double? ClearThreshold { get; set; }

    /// <summary>The condition must hold this long before an alert is raised.</summary>
    public int DurationSec { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public class Alert
{
    public long Id { get; set; }
    public int? RuleId { get; set; }
    public int? DeviceId { get; set; }
    public int? InterfaceId { get; set; }

    /// <summary>Rule + entity; only one open (Active/Acknowledged) alert may exist per key.</summary>
    public string DedupKey { get; set; } = string.Empty;
    public Severity Severity { get; set; }
    public AlertState State { get; set; } = AlertState.Active;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public double? CurrentValue { get; set; }
    public bool AutoResolve { get; set; } = true;
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public int? AckByUserId { get; set; }
    public string? AckByUsername { get; set; }
    public DateTime? AckUtc { get; set; }
    public string? AckComment { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public DateTime? ClosedUtc { get; set; }
    public string? ClosedByUsername { get; set; }

    public List<AlertHistoryEntry> History { get; set; } = new();
}

public class AlertHistoryEntry
{
    public long Id { get; set; }
    public long AlertId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Comment { get; set; }
}
