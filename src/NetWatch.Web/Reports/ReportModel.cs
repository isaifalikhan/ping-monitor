using NetWatch.Core.Entities;

namespace NetWatch.Web.Reports;

/// <summary>What to report on: a period (UTC, end exclusive) and optionally one group (with its subgroups).</summary>
public sealed record ReportRequest(ReportType Type, DateTime FromUtc, DateTime ToUtc, int? GroupId = null);

/// <summary>A finished report, independent of the output format.</summary>
public sealed class ReportDocument
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public List<ReportSection> Sections { get; } = new();
}

public sealed class ReportSection
{
    public required string Heading { get; init; }
    public string? Note { get; init; }

    /// <summary>Key figures shown as a small grid.</summary>
    public List<(string Label, string Value)> Summary { get; } = new();

    /// <summary>Optional horizontal bar chart (e.g. the worst 15 devices).</summary>
    public ReportBars? Bars { get; set; }
    public ReportTable? Table { get; set; }
}

/// <param name="Max">Full-scale value of the bars (100 for percentages).</param>
public sealed record ReportBars(IReadOnlyList<(string Label, double Value, string Text)> Items, double Max, bool LowIsBad = false);

/// <param name="NumericColumns">Indexes of right-aligned columns.</param>
public sealed record ReportTable(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows, IReadOnlySet<int>? NumericColumns = null, string? Empty = null);

public static class ReportCatalog
{
    public static readonly IReadOnlyList<(ReportType Type, string Name, string Description, bool HasPeriod)> Types =
    [
        (ReportType.Availability, "Availability", "Uptime per device (ping/SNMP), outages, approximate downtime and latency, worst devices first.", true),
        (ReportType.Alerts, "Alert history", "Alerts raised in the period by severity, rule and device, with time to acknowledge and to resolve.", true),
        (ReportType.Interfaces, "Interface utilization", "Average and peak in/out utilization of important interfaces (uplinks, server ports), busiest first.", true),
        (ReportType.Capacity, "Server capacity", "CPU and memory trends of servers, volume and datastore fill levels with growth, and disks predicted to fill within 30 days.", true),
        (ReportType.Security, "Domain controller security", "Failed logons, lockouts, privileged group changes, new accounts and audit events from the DC Security logs.", true),
        (ReportType.Inventory, "Device inventory", "All devices with type, address, group, vendor, model, serial number, firmware and status (current state).", false),
        (ReportType.Vmware, "VMware", "ESXi hosts (CPU, memory, state), datastore capacity, snapshots with age and size, and powered-off VMs (current state).", false),
    ];

    public static string NameOf(ReportType type) => Types.First(t => t.Type == type).Name;
}
