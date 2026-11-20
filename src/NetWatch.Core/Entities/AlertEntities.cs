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

    /// <summary>Metric key (Threshold, Absence), state key (StateChange) or event key (EventMatch, CountInWindow). See <see cref="Monitoring.MetricKeys"/>.</summary>
    public string Key { get; set; } = string.Empty;
    public ComparisonOperator? Operator { get; set; }
    public double? Threshold { get; set; }

    /// <summary>Optional hysteresis: the alert only clears once the value passes this level.</summary>
    public double? ClearThreshold { get; set; }

    /// <summary>The condition must hold this long before an alert is raised (Absence: the silence period).</summary>
    public int DurationSec { get; set; }

    /// <summary>JSON-serialised <see cref="Alerts.EventFilter"/> for event rules (message pattern, event IDs ...).</summary>
    public string? FilterJson { get; set; }

    /// <summary>CountInWindow: the window length in seconds.</summary>
    public int WindowSec { get; set; }

    /// <summary>CountInWindow: number of matching events within the window that raises the alert.</summary>
    public int CountThreshold { get; set; }

    /// <summary>Escalate (flag and repeat the sound) when still unacknowledged after this many minutes. Null = use the global setting for the severity.</summary>
    public int? EscalateAfterMin { get; set; }
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

    /// <summary>The condition changed state often within the flap window; resolve is held until it is stable.</summary>
    public bool IsFlapping { get; set; }

    /// <summary>Number of state changes counted when flapping was detected.</summary>
    public int FlapCount { get; set; }

    /// <summary>Set when the alert was escalated (not acknowledged in time).</summary>
    public DateTime? EscalatedUtc { get; set; }
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

/// <summary>
/// A period during which no new alerts are raised for the devices in scope (polling continues). One-off or
/// recurring; recurring times are local (Asia/Karachi).
/// </summary>
public class MaintenanceWindow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    /// <summary>JSON-serialised <see cref="Alerts.RuleScope"/> (devices, groups, types, tags). Empty = all devices.</summary>
    public string ScopeJson { get; set; } = "{}";
    public MaintenanceRecurrence Recurrence { get; set; }

    /// <summary>Once: window start. Recurring: first day the schedule applies (optional).</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>Once: window end. Recurring: last day the schedule applies (optional).</summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>Daily/Weekly: local start time "HH:mm".</summary>
    public string? StartTimeLocal { get; set; }

    /// <summary>Weekly: comma-separated days ("Mon,Wed,Fri").</summary>
    public string? DaysOfWeek { get; set; }

    /// <summary>Cron recurrence (5 fields, local time).</summary>
    public string? CronExpression { get; set; }

    /// <summary>Recurring windows: length in minutes.</summary>
    public int DurationMin { get; set; }
    public string? Reason { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
