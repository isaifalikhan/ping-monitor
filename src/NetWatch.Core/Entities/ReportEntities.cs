namespace NetWatch.Core.Entities;

public enum ReportType
{
    Availability,
    Alerts,
    Interfaces,
    Capacity,
    Security,
    Inventory
}

public enum ReportFormat
{
    Pdf,
    Csv
}

/// <summary>How often a scheduled report runs; each run covers the previous day, 7 days or calendar month.</summary>
public enum ReportFrequency
{
    Daily,
    Weekly,
    Monthly
}

/// <summary>A report generated automatically, saved to the reports folder and optionally e-mailed.</summary>
public class ReportSchedule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public ReportType ReportType { get; set; }
    public ReportFormat Format { get; set; }
    public ReportFrequency Frequency { get; set; }

    /// <summary>Local (PKT) run time "HH:mm".</summary>
    public string TimeOfDay { get; set; } = "07:00";

    /// <summary>Weekly: day of the week (0 = Sunday … 6 = Saturday).</summary>
    public int DayOfWeek { get; set; } = 1;

    /// <summary>Monthly: day of the month (1–28).</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Only devices of this group (and subgroups); null = all.</summary>
    public int? GroupId { get; set; }

    /// <summary>Comma-separated e-mail recipients (needs the SMTP channel); empty = save only.</summary>
    public string? EmailTo { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public string? LastStatus { get; set; }
    public string? LastFile { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
