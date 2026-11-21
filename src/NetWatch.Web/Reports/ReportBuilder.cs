using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Core.Metrics;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Api;

namespace NetWatch.Web.Reports;

/// <summary>Collects the data of a report from the databases and the metric store.</summary>
public sealed class ReportBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly NetWatchDbContext _db;
    private readonly EventsDbContext _events;
    private readonly IMetricStore _metrics;
    private readonly SettingsService _settings;

    public ReportBuilder(NetWatchDbContext db, EventsDbContext events, IMetricStore metrics, SettingsService settings)
    {
        _db = db;
        _events = events;
        _metrics = metrics;
        _settings = settings;
    }

    public async Task<ReportDocument> BuildAsync(ReportRequest request, CancellationToken ct)
    {
        var groups = await _db.Groups.AsNoTracking().ToListAsync(ct);
        var paths = DeviceStatusHelper.GroupPaths(groups);
        HashSet<int>? groupFilter = request.GroupId is { } gid ? DeviceEndpoints.Subtree(groups, gid) : null;
        var devices = (await _db.Devices.AsNoTracking().Where(d => d.IsEnabled).OrderBy(d => d.Name).ToListAsync(ct))
            .Where(d => groupFilter is null || (d.GroupId is { } g && groupFilter.Contains(g)))
            .ToList();

        var scope = request.GroupId is { } id && paths.TryGetValue(id, out var groupName) ? $" · group {groupName}" : string.Empty;
        var hasPeriod = ReportCatalog.Types.First(t => t.Type == request.Type).HasPeriod;
        var doc = new ReportDocument
        {
            Title = ReportCatalog.NameOf(request.Type),
            Subtitle = (hasPeriod ? $"{Local(request.FromUtc)} – {Local(request.ToUtc)} PKT" : $"State at {Local(DateTime.UtcNow)} PKT")
                       + scope + $" · generated {Local(DateTime.UtcNow)} PKT by NetWatch",
        };

        var ctx = new Context(request, devices, paths, doc);
        switch (request.Type)
        {
            case ReportType.Availability:
                await AvailabilityAsync(ctx, ct);
                break;
            case ReportType.Alerts:
                await AlertsAsync(ctx, ct);
                break;
            case ReportType.Interfaces:
                await InterfacesAsync(ctx, ct);
                break;
            case ReportType.Capacity:
                await CapacityAsync(ctx, ct);
                break;
            case ReportType.Security:
                await SecurityAsync(ctx, ct);
                break;
            case ReportType.Inventory:
                await InventoryAsync(ctx, ct);
                break;
            case ReportType.Vmware:
                await VmwareAsync(ctx, ct);
                break;
        }

        return doc;
    }

    private sealed record Context(ReportRequest Request, List<Device> Devices, Dictionary<int, string> GroupPaths, ReportDocument Doc)
    {
        public string Group(Device d) => d.GroupId is { } g ? GroupPaths.GetValueOrDefault(g, string.Empty) : string.Empty;
    }

    // ------------------------------------------------------------------ availability

    private async Task AvailabilityAsync(Context c, CancellationToken ct)
    {
        var (from, to) = (c.Request.FromUtc, c.Request.ToUtc);
        var outages = (await _events.DeviceEvents.AsNoTracking()
                .Where(e => e.Kind == DeviceEventKind.StatusChange && e.TimestampUtc >= from && e.TimestampUtc < to && e.Message.Contains("to Down"))
                .Select(e => e.DeviceId)
                .ToListAsync(ct))
            .Where(id => id.HasValue)
            .GroupBy(id => id!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var rows = new List<(Device Device, double? Availability, double? Rtt, int Outages)>();
        foreach (var d in c.Devices)
        {
            var data = await _metrics.QueryDeviceAsync(d.Id, from, to, ct);
            var up = Column(data, "Up");
            var rtt = Column(data, "RttMs");
            var ups = data.Points.Select(p => p.Avg[up]).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            var rtts = data.Points.Select(p => p.Avg[rtt]).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            rows.Add((d, ups.Count == 0 ? null : 100 * ups.Average(), rtts.Count == 0 ? null : rtts.Average(), outages.GetValueOrDefault(d.Id)));
        }

        var measured = rows.Where(r => r.Availability.HasValue).ToList();
        var period = to - from;
        var section = new ReportSection
        {
            Heading = "Summary",
            Note = "Availability is the share of polls in which the device answered ping or SNMP (from the stored history: raw, 5-minute or hourly averages depending on the period). Downtime is approximate. Devices without data in the period are listed at the end.",
        };
        section.Summary.Add(("Devices", c.Devices.Count.ToString(Inv)));
        section.Summary.Add(("Average availability", measured.Count == 0 ? "–" : Pct(measured.Average(r => r.Availability!.Value), 3)));
        section.Summary.Add(("Below 99.9 %", measured.Count(r => r.Availability < 99.9).ToString(Inv)));
        section.Summary.Add(("Outages", rows.Sum(r => r.Outages).ToString(Inv)));
        c.Doc.Sections.Add(section);

        var worst = measured.OrderBy(r => r.Availability).ThenByDescending(r => r.Outages).Take(15).ToList();
        if (worst.Count > 0)
        {
            c.Doc.Sections.Add(new ReportSection
            {
                Heading = "Least available devices",
                Bars = new ReportBars(worst.Select(r => (r.Device.Name, r.Availability!.Value, Pct(r.Availability.Value, 3))).ToList(), 100, LowIsBad: true),
            });
        }

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "All devices",
            Table = new ReportTable(
                ["Device", "Type", "IP address", "Group", "Availability", "Downtime (approx.)", "Outages", "Avg latency"],
                rows.OrderBy(r => r.Availability ?? double.MaxValue).ThenBy(r => r.Device.Name)
                    .Select(r => new[]
                    {
                        r.Device.Name, r.Device.Type.ToString(), r.Device.IpAddress, c.Group(r.Device),
                        r.Availability is { } a ? Pct(a, 3) : "no data",
                        r.Availability is { } a2 ? Duration(TimeSpan.FromTicks((long)(period.Ticks * (100 - a2) / 100))) : "–",
                        r.Outages.ToString(Inv),
                        r.Rtt is { } ms ? ms.ToString("0.0", Inv) + " ms" : "–",
                    }).ToList(),
                new HashSet<int> { 4, 5, 6, 7 }),
        });
    }

    // ------------------------------------------------------------------ alerts

    private async Task AlertsAsync(Context c, CancellationToken ct)
    {
        var (from, to) = (c.Request.FromUtc, c.Request.ToUtc);
        var ids = c.Devices.Select(d => d.Id).ToHashSet();
        var names = c.Devices.ToDictionary(d => d.Id, d => d.Name);
        var alerts = (await _db.Alerts.AsNoTracking().Where(a => a.FirstSeenUtc >= from && a.FirstSeenUtc < to).OrderByDescending(a => a.FirstSeenUtc).ToListAsync(ct))
            .Where(a => a.DeviceId is null ? c.Request.GroupId is null : ids.Contains(a.DeviceId.Value))
            .ToList();
        var rules = await _db.AlertRules.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct);

        var acked = alerts.Where(a => a.AckUtc.HasValue).ToList();
        var resolved = alerts.Where(a => a.ResolvedUtc.HasValue).ToList();
        var summary = new ReportSection { Heading = "Summary" };
        summary.Summary.Add(("Alerts raised", alerts.Count.ToString(Inv)));
        summary.Summary.Add(("Critical / Major", $"{alerts.Count(a => a.Severity == Severity.Critical)} / {alerts.Count(a => a.Severity == Severity.Major)}"));
        summary.Summary.Add(("Warning / Info", $"{alerts.Count(a => a.Severity == Severity.Warning)} / {alerts.Count(a => a.Severity == Severity.Info)}"));
        summary.Summary.Add(("Still open", alerts.Count(a => a.State is AlertState.Active or AlertState.Acknowledged).ToString(Inv)));
        summary.Summary.Add(("Acknowledged", alerts.Count == 0 ? "–" : Pct(100.0 * acked.Count / alerts.Count, 0)));
        summary.Summary.Add(("Mean time to acknowledge", acked.Count == 0 ? "–" : Duration(TimeSpan.FromSeconds(acked.Average(a => (a.AckUtc!.Value - a.FirstSeenUtc).TotalSeconds)))));
        summary.Summary.Add(("Mean time to resolve", resolved.Count == 0 ? "–" : Duration(TimeSpan.FromSeconds(resolved.Average(a => (a.ResolvedUtc!.Value - a.FirstSeenUtc).TotalSeconds)))));
        summary.Summary.Add(("Escalated", alerts.Count(a => a.EscalatedUtc.HasValue).ToString(Inv)));
        c.Doc.Sections.Add(summary);

        var topDevices = alerts.Where(a => a.DeviceId.HasValue).GroupBy(a => a.DeviceId!.Value)
            .Select(g => (Name: names.GetValueOrDefault(g.Key, $"#{g.Key}"), Count: g.Count())).OrderByDescending(x => x.Count).Take(15).ToList();
        if (topDevices.Count > 0)
        {
            c.Doc.Sections.Add(new ReportSection
            {
                Heading = "Devices with most alerts",
                Bars = new ReportBars(topDevices.Select(x => (x.Name, (double)x.Count, x.Count.ToString(Inv))).ToList(), topDevices.Max(x => x.Count)),
            });
        }

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Alerts by rule",
            Table = new ReportTable(
                ["Rule", "Alerts", "Critical", "Major", "Warning", "Info", "Devices"],
                alerts.GroupBy(a => a.RuleId is { } r ? rules.GetValueOrDefault(r, $"rule {r}") : "(no rule)")
                    .OrderByDescending(g => g.Count())
                    .Select(g => new[]
                    {
                        g.Key, g.Count().ToString(Inv),
                        g.Count(a => a.Severity == Severity.Critical).ToString(Inv), g.Count(a => a.Severity == Severity.Major).ToString(Inv),
                        g.Count(a => a.Severity == Severity.Warning).ToString(Inv), g.Count(a => a.Severity == Severity.Info).ToString(Inv),
                        g.Select(a => a.DeviceId).Distinct().Count().ToString(Inv),
                    }).ToList(),
                new HashSet<int> { 1, 2, 3, 4, 5, 6 }),
        });

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = alerts.Count > 1000 ? "Alerts (newest 1000)" : "Alerts",
            Table = new ReportTable(
                ["Raised (PKT)", "Severity", "Alert", "State", "Acknowledged by", "Duration"],
                alerts.Take(1000).Select(a => new[]
                {
                    Local(a.FirstSeenUtc), a.Severity.ToString(), a.Title, a.State.ToString(), a.AckByUsername ?? string.Empty,
                    Duration(((a.ResolvedUtc ?? a.ClosedUtc) ?? DateTime.UtcNow) - a.FirstSeenUtc),
                }).ToList(),
                new HashSet<int> { 5 }),
        });
    }

    // ------------------------------------------------------------------ interfaces

    private async Task InterfacesAsync(Context c, CancellationToken ct)
    {
        var (from, to) = (c.Request.FromUtc, c.Request.ToUtc);
        var ids = c.Devices.Select(d => d.Id).ToHashSet();
        var names = c.Devices.ToDictionary(d => d.Id, d => d.Name);
        var interfaces = (await _db.Interfaces.AsNoTracking().Where(i => i.IsImportant && i.IsMonitored).ToListAsync(ct))
            .Where(i => ids.Contains(i.DeviceId))
            .Take(2000)
            .ToList();

        var rows = new List<(NetInterface If, double? AvgIn, double? AvgOut, double? PeakIn, double? PeakOut, double? Errors)>();
        foreach (var i in interfaces)
        {
            var data = await _metrics.QueryInterfaceAsync(i.Id, from, to, ct);
            if (data.Points.Count == 0)
            {
                rows.Add((i, null, null, null, null, null));
                continue;
            }

            int inU = Column(data, "InUtilPct"), outU = Column(data, "OutUtilPct"), inE = Column(data, "InErrPs"), outE = Column(data, "OutErrPs");
            double? Avg(int col) => Values(data, col, max: false) is { Count: > 0 } v ? v.Average() : null;
            double? PeakOf(int col) => Values(data, col, max: true) is { Count: > 0 } v ? v.Max() : null;
            var errors = (Avg(inE) ?? 0) + (Avg(outE) ?? 0);
            rows.Add((i, Avg(inU), Avg(outU), PeakOf(inU), PeakOf(outU), errors));
        }

        double Peak((NetInterface If, double? AvgIn, double? AvgOut, double? PeakIn, double? PeakOut, double? Errors) r) => Math.Max(r.PeakIn ?? 0, r.PeakOut ?? 0);

        var summary = new ReportSection
        {
            Heading = "Summary",
            Note = "Important interfaces only (uplinks and server ports flagged as important). Peaks come from the 5-minute or hourly maxima, so short bursts are smoothed for long periods.",
        };
        summary.Summary.Add(("Interfaces", interfaces.Count.ToString(Inv)));
        summary.Summary.Add(("Peak above 80 %", rows.Count(r => Peak(r) > 80).ToString(Inv)));
        summary.Summary.Add(("Average above 50 %", rows.Count(r => Math.Max(r.AvgIn ?? 0, r.AvgOut ?? 0) > 50).ToString(Inv)));
        summary.Summary.Add(("With errors", rows.Count(r => r.Errors > 0.01).ToString(Inv)));
        c.Doc.Sections.Add(summary);

        var busiest = rows.Where(r => r.PeakIn.HasValue || r.PeakOut.HasValue).OrderByDescending(Peak).Take(15).ToList();
        if (busiest.Count > 0)
        {
            c.Doc.Sections.Add(new ReportSection
            {
                Heading = "Busiest interfaces (peak utilization)",
                Bars = new ReportBars(busiest.Select(r => ($"{names.GetValueOrDefault(r.If.DeviceId)} {r.If.Name}", Peak(r), Pct(Peak(r), 1))).ToList(), 100),
            });
        }

        var withErrors = rows.Where(r => r.Errors > 0.01).OrderByDescending(r => r.Errors).Take(15).ToList();
        if (withErrors.Count > 0)
        {
            c.Doc.Sections.Add(new ReportSection
            {
                Heading = "Most errors (average errors per second)",
                Table = new ReportTable(["Device", "Interface", "Alias", "Errors/s", "Avg in", "Avg out"],
                    withErrors.Select(r => new[]
                    {
                        names.GetValueOrDefault(r.If.DeviceId, string.Empty), r.If.Name, r.If.Alias ?? string.Empty, r.Errors!.Value.ToString("0.##", Inv), Opt(r.AvgIn), Opt(r.AvgOut),
                    }).ToList(),
                    new HashSet<int> { 3, 4, 5 }),
            });
        }

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Important interfaces",
            Table = new ReportTable(
                ["Device", "Interface", "Alias", "Speed", "Avg in", "Avg out", "Peak in", "Peak out", "Errors/s"],
                rows.OrderByDescending(Peak).Select(r => new[]
                {
                    names.GetValueOrDefault(r.If.DeviceId, string.Empty), r.If.Name, r.If.Alias ?? string.Empty, Speed(r.If.SpeedBps),
                    Opt(r.AvgIn), Opt(r.AvgOut), Opt(r.PeakIn), Opt(r.PeakOut), r.Errors is { } e ? e.ToString("0.##", Inv) : "–",
                }).ToList(),
                new HashSet<int> { 3, 4, 5, 6, 7, 8 },
                "No important interfaces. Flag uplinks as important on the device page."),
        });

        static string Opt(double? v) => v is { } x ? Pct(x, 1) : "–";
    }

    // ------------------------------------------------------------------ capacity

    private async Task CapacityAsync(Context c, CancellationToken ct)
    {
        var (from, to) = (c.Request.FromUtc, c.Request.ToUtc);
        var ids = c.Devices.Select(d => d.Id).ToHashSet();
        var names = c.Devices.ToDictionary(d => d.Id, d => d.Name);
        var volumes = (await _db.Volumes.AsNoTracking().ToListAsync(ct)).Where(v => ids.Contains(v.DeviceId)).ToList();
        var days = Math.Max((to - from).TotalDays, 1 / 24.0);

        // Growth from the first and last stored value of the "disk used %" series in the period.
        async Task<double?> GrowthAsync(int deviceId, string key, string instance)
        {
            var series = _metrics.ListSeries(deviceId).FirstOrDefault(s => s.Key == key && string.Equals(s.Instance, instance, StringComparison.OrdinalIgnoreCase));
            if (series is null)
            {
                return null;
            }

            var data = await _metrics.QuerySeriesAsync(series.Id, from, to, ct);
            var values = data.Points.Select(p => p.Avg[0]).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return values.Count < 2 ? null : values[^1] - values[0];
        }

        var rows = new List<(string Device, string Name, string? Label, long Total, long Free, double Used, double? Growth)>();
        foreach (var v in volumes)
        {
            rows.Add((names[v.DeviceId], v.Name, v.Label, v.TotalBytes, v.FreeBytes, v.UsedPct, await GrowthAsync(v.DeviceId, MetricKeys.DiskUsed, v.Name)));
        }

        var datastores = c.Request.GroupId is null ? await _db.VmwareDatastores.AsNoTracking().ToListAsync(ct) : [];
        var dsRows = new List<(string Name, long Total, long Free, double Used, double? Growth)>();
        foreach (var d in datastores)
        {
            var used = d.CapacityBytes <= 0 ? 0 : Math.Round(100.0 * (d.CapacityBytes - d.FreeBytes) / d.CapacityBytes, 1);
            dsRows.Add((d.Name, d.CapacityBytes, d.FreeBytes, used, await GrowthAsync(d.SourceDeviceId, MetricKeys.DatastoreUsed, d.Name)));
        }

        string DaysToFull(double used, double? growth) =>
            CapacityMath.DaysUntilFull(used, growth, days) is not { } remaining ? "stable"
            : remaining > 3650 ? "> 10 years" : remaining.ToString("0", Inv) + " days";

        // CPU and memory per server: average, peak and the trend (second half of the period minus the first).
        var serverRows = new List<(Device Device, double? CpuAvg, double? CpuPeak, double? CpuTrend, double? MemAvg, double? MemPeak, double? MemTrend)>();
        foreach (var d in c.Devices.Where(d => d.Type is DeviceType.Server or DeviceType.VM or DeviceType.DomainController or DeviceType.Hypervisor))
        {
            var data = await _metrics.QueryDeviceAsync(d.Id, from, to, ct);
            int cpu = Column(data, "CpuPct"), mem = Column(data, "MemPct");
            if (data.Points.Count == 0 || cpu < 0 || mem < 0)
            {
                continue;
            }

            var cpuValues = Values(data, cpu, max: false);
            var memValues = Values(data, mem, max: false);
            if (cpuValues.Count == 0 && memValues.Count == 0)
            {
                continue;
            }

            serverRows.Add((d, Mean(cpuValues), Values(data, cpu, max: true) is { Count: > 0 } cp ? cp.Max() : null, CapacityMath.Trend(cpuValues),
                Mean(memValues), Values(data, mem, max: true) is { Count: > 0 } mp ? mp.Max() : null, CapacityMath.Trend(memValues)));
        }

        static double? Mean(List<double> v) => v.Count == 0 ? null : v.Average();
        static string Trend(double? t) => t is { } x ? (x >= 0 ? "+" : string.Empty) + x.ToString("0.0", Inv) + " pt" : "–";

        var summary = new ReportSection
        {
            Heading = "Summary",
            Note = "Fill levels are the latest values. Growth compares the first and last stored value in the period; 'days until full' assumes the same linear growth continues.",
        };
        summary.Summary.Add(("Volumes", rows.Count.ToString(Inv)));
        summary.Summary.Add(("Above 85 %", rows.Count(r => r.Used > 85).ToString(Inv)));
        summary.Summary.Add(("Above 95 %", rows.Count(r => r.Used > 95).ToString(Inv)));
        summary.Summary.Add(("Datastores", dsRows.Count.ToString(Inv)));
        var soon = rows.Select(r => (Device: r.Device, r.Name, r.Used, r.Growth, Days: CapacityMath.DaysUntilFull(r.Used, r.Growth, days)))
            .Concat(dsRows.Select(r => (Device: "datastore", r.Name, r.Used, r.Growth, Days: CapacityMath.DaysUntilFull(r.Used, r.Growth, days))))
            .Where(r => r.Days is <= 30)
            .OrderBy(r => r.Days)
            .ToList();
        summary.Summary.Add(("Full within 30 days", soon.Count.ToString(Inv)));
        summary.Summary.Add(("Servers with CPU peak > 90 %", serverRows.Count(r => r.CpuPeak > 90).ToString(Inv)));
        c.Doc.Sections.Add(summary);

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Predicted to fill within 30 days",
            Table = new ReportTable(["Device", "Volume / datastore", "Used", "Growth in period", "Days until full"],
                soon.Select(r => new[] { r.Device, r.Name, Pct(r.Used, 1), Trend(r.Growth), r.Days!.Value.ToString("0", Inv) }).ToList(),
                new HashSet<int> { 2, 3, 4 }, "No volume or datastore is predicted to fill within 30 days."),
        });

        if (serverRows.Count > 0)
        {
            c.Doc.Sections.Add(new ReportSection
            {
                Heading = "Server CPU and memory",
                Note = "Trend = average of the second half of the period minus the first half (percentage points).",
                Table = new ReportTable(["Server", "Group", "CPU avg", "CPU peak", "CPU trend", "Memory avg", "Memory peak", "Memory trend"],
                    serverRows.OrderByDescending(r => r.CpuPeak ?? 0).Select(r => new[]
                    {
                        r.Device.Name, c.Group(r.Device), r.CpuAvg is { } a ? Pct(a, 1) : "–", r.CpuPeak is { } p ? Pct(p, 1) : "–", Trend(r.CpuTrend),
                        r.MemAvg is { } m ? Pct(m, 1) : "–", r.MemPeak is { } mp ? Pct(mp, 1) : "–", Trend(r.MemTrend),
                    }).ToList(),
                    new HashSet<int> { 2, 3, 4, 5, 6, 7 }),
            });
        }

        var fullest = rows.Concat(dsRows.Select(d => ("datastore", d.Name, (string?)null, d.Total, d.Free, d.Used, d.Growth)))
            .OrderByDescending(r => r.Item6).Take(15).ToList();
        if (fullest.Count > 0)
        {
            c.Doc.Sections.Add(new ReportSection
            {
                Heading = "Fullest volumes and datastores",
                Bars = new ReportBars(fullest.Select(r => ($"{r.Item1} {r.Item2}", r.Item6, Pct(r.Item6, 1))).ToList(), 100),
            });
        }

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Server volumes",
            Table = new ReportTable(
                ["Device", "Volume", "Label", "Size", "Free", "Used", "Growth", "Days until full"],
                rows.OrderByDescending(r => r.Used).Select(r => new[]
                {
                    r.Device, r.Name, r.Label ?? string.Empty, Bytes(r.Total), Bytes(r.Free), Pct(r.Used, 1),
                    r.Growth is { } g ? (g >= 0 ? "+" : string.Empty) + g.ToString("0.0", Inv) + " pt" : "–", DaysToFull(r.Used, r.Growth),
                }).ToList(),
                new HashSet<int> { 3, 4, 5, 6, 7 },
                "No server volumes (servers need a WinRM/SSH credential or SNMP)."),
        });

        if (dsRows.Count > 0)
        {
            c.Doc.Sections.Add(new ReportSection
            {
                Heading = "VMware datastores",
                Table = new ReportTable(
                    ["Datastore", "Capacity", "Free", "Used", "Growth", "Days until full"],
                    dsRows.OrderByDescending(r => r.Used).Select(r => new[]
                    {
                        r.Name, Bytes(r.Total), Bytes(r.Free), Pct(r.Used, 1),
                        r.Growth is { } g ? (g >= 0 ? "+" : string.Empty) + g.ToString("0.0", Inv) + " pt" : "–", DaysToFull(r.Used, r.Growth),
                    }).ToList(),
                    new HashSet<int> { 1, 2, 3, 4, 5 }),
            });
        }
    }

    // ------------------------------------------------------------------ DC security

    private async Task SecurityAsync(Context c, CancellationToken ct)
    {
        var (from, to) = (c.Request.FromUtc, c.Request.ToUtc);
        var dcs = c.Devices.Where(d => d.Type == DeviceType.DomainController).ToDictionary(d => d.Id, d => d.Name);
        var dcIds = dcs.Keys.ToList();
        var q = _events.DcSecurityEvents.AsNoTracking().Where(e => e.TimestampUtc >= from && e.TimestampUtc < to && dcIds.Contains(e.DeviceId));
        var failedQ = q.Where(e => e.EventId == 4625 || e.EventId == 4771 || ((e.EventId == 4776 || e.EventId == 4768) && e.Status != null && e.Status != "0x0"));
        int[] groupIds = [4728, 4729, 4732, 4733, 4756, 4757];

        var failedCount = await failedQ.CountAsync(ct);
        var topAccounts = await failedQ.Where(e => e.TargetUser != null).GroupBy(e => e.TargetUser!)
            .Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(x => x.Count).Take(15).ToListAsync(ct);
        var topSources = await failedQ.Where(e => e.SourceIp != null || e.Workstation != null).GroupBy(e => e.SourceIp ?? e.Workstation!)
            .Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(x => x.Count).Take(15).ToListAsync(ct);
        var lockouts = await q.Where(e => e.EventId == 4740).OrderByDescending(e => e.TimestampUtc).Take(500).ToListAsync(ct);
        var groupChanges = await q.Where(e => groupIds.Contains(e.EventId)).OrderByDescending(e => e.TimestampUtc).Take(1000).ToListAsync(ct);
        var notable = await q.Where(e => e.EventId == 4720 || e.EventId == 4726 || e.EventId == 4741 || e.EventId == 4743 || e.EventId == 4719 || e.EventId == 1102)
            .OrderByDescending(e => e.TimestampUtc).Take(1000).ToListAsync(ct);
        var dc = _settings.Dc;
        var privileged = groupChanges.Where(e => dc.IsPrivilegedGroup(e.GroupName)).ToList();

        var summary = new ReportSection
        {
            Heading = "Summary",
            Note = dcs.Count == 0 ? "No domain controller in scope." : $"Domain controllers: {string.Join(", ", dcs.Values.Order())}.",
        };
        summary.Summary.Add(("Failed logons", failedCount.ToString(Inv)));
        summary.Summary.Add(("Lockouts", lockouts.Count.ToString(Inv)));
        summary.Summary.Add(("Privileged group changes", privileged.Count.ToString(Inv)));
        summary.Summary.Add(("Group changes (all)", groupChanges.Count.ToString(Inv)));
        summary.Summary.Add(("Accounts created", notable.Count(e => e.EventId == 4720).ToString(Inv)));
        summary.Summary.Add(("Computer accounts created", notable.Count(e => e.EventId == 4741).ToString(Inv)));
        summary.Summary.Add(("Audit policy changes", notable.Count(e => e.EventId == 4719).ToString(Inv)));
        summary.Summary.Add(("Security log cleared", notable.Count(e => e.EventId == 1102).ToString(Inv)));
        c.Doc.Sections.Add(summary);

        if (topAccounts.Count > 0)
        {
            c.Doc.Sections.Add(new ReportSection
            {
                Heading = "Accounts with most failed logons",
                Bars = new ReportBars(topAccounts.Select(x => (x.Key, (double)x.Count, x.Count.ToString(Inv))).ToList(), topAccounts.Max(x => x.Count)),
            });
        }

        if (topSources.Count > 0)
        {
            c.Doc.Sections.Add(new ReportSection
            {
                Heading = "Sources with most failed logons",
                Bars = new ReportBars(topSources.Select(x => (x.Key, (double)x.Count, x.Count.ToString(Inv))).ToList(), topSources.Max(x => x.Count)),
            });
        }

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Privileged group changes",
            Table = new ReportTable(["Time (PKT)", "Change", "Group", "Member", "Changed by", "DC"],
                privileged.Select(e => new[]
                {
                    Local(e.TimestampUtc), DcEventCatalog.GroupAdds.Contains(e.EventId) ? "added" : "removed", e.GroupName ?? string.Empty,
                    e.TargetUser ?? string.Empty, e.SubjectUser ?? string.Empty, dcs.GetValueOrDefault(e.DeviceId, string.Empty),
                }).ToList(), null, "No changes to privileged groups."),
        });

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Account lockouts",
            Table = new ReportTable(["Time (PKT)", "Account", "Caller computer", "DC"],
                lockouts.Select(e => new[] { Local(e.TimestampUtc), e.TargetUser ?? string.Empty, e.Workstation ?? string.Empty, dcs.GetValueOrDefault(e.DeviceId, string.Empty) }).ToList(),
                null, "No lockouts."),
        });

        var adminLogons = await q.Where(e => e.EventId == 4672).OrderByDescending(e => e.TimestampUtc).Take(20_000).ToListAsync(ct);
        var offHours = adminLogons.Where(e => !dc.IsWorkingTime(e.TimestampUtc)).Take(1000).ToList();
        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Admin logons outside working hours",
            Note = $"Special privileges assigned to a logon (4672) outside {dc.WorkingDays} {dc.WorkingHoursStart}–{dc.WorkingHoursEnd} PKT.",
            Table = new ReportTable(["Time (PKT)", "Account", "DC"],
                offHours.Select(e => new[] { Local(e.TimestampUtc), e.SubjectDomain is null ? e.SubjectUser ?? string.Empty : $"{e.SubjectDomain}\\{e.SubjectUser}", dcs.GetValueOrDefault(e.DeviceId, string.Empty) }).ToList(),
                null, "No admin logons outside working hours."),
        });

        var cleared = notable.Where(e => e.EventId is 1102 or 4719).ToList();
        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Security log cleared / audit policy changed",
            Table = new ReportTable(["Time (PKT)", "Event", "By", "DC"],
                cleared.Select(e => new[] { Local(e.TimestampUtc), $"{e.EventId} {DcEventCatalog.NameOf(e.EventId)}", e.SubjectUser ?? string.Empty, dcs.GetValueOrDefault(e.DeviceId, string.Empty) }).ToList(),
                null, "The Security log was not cleared and the audit policy was not changed."),
        });

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Account and policy changes",
            Table = new ReportTable(["Time (PKT)", "Event", "Details", "DC"],
                notable.Select(e => new[] { Local(e.TimestampUtc), $"{e.EventId} {DcEventCatalog.NameOf(e.EventId)}", e.Message, dcs.GetValueOrDefault(e.DeviceId, string.Empty) }).ToList(),
                null, "No account creations, deletions, audit policy changes or log clearing."),
        });
    }

    // ------------------------------------------------------------------ VMware

    private async Task VmwareAsync(Context c, CancellationToken ct)
    {
        var hosts = await _db.VmwareHosts.AsNoTracking().OrderBy(h => h.Name).ToListAsync(ct);
        var datastores = await _db.VmwareDatastores.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        var snapshots = await _db.VmwareSnapshots.AsNoTracking().OrderBy(s => s.CreatedUtc).ToListAsync(ct);
        var vms = await _db.VmwareVms.AsNoTracking().OrderBy(v => v.Name).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var off = vms.Where(v => !v.PowerState.Equals("poweredOn", StringComparison.OrdinalIgnoreCase)).ToList();

        var summary = new ReportSection { Heading = "Summary" };
        summary.Summary.Add(("ESXi hosts", hosts.Count.ToString(Inv)));
        summary.Summary.Add(("Hosts not connected", hosts.Count(h => !h.ConnectionState.Equals("connected", StringComparison.OrdinalIgnoreCase)).ToString(Inv)));
        summary.Summary.Add(("VMs", vms.Count.ToString(Inv)));
        summary.Summary.Add(("Powered off", off.Count.ToString(Inv)));
        summary.Summary.Add(("Datastores", datastores.Count.ToString(Inv)));
        summary.Summary.Add(("Snapshots", snapshots.Count.ToString(Inv)));
        summary.Summary.Add(("Snapshots older than 7 days", snapshots.Count(s => now - s.CreatedUtc > TimeSpan.FromDays(7)).ToString(Inv)));
        c.Doc.Sections.Add(summary);

        static string P(long? used, long? total) => used is { } u && total is { } t && t > 0 ? Pct(100.0 * u / t, 1) : "–";
        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "ESXi hosts",
            Table = new ReportTable(["Host", "Cluster", "State", "Maintenance", "Model", "CPU", "Memory", "VMs", "Status"],
                hosts.Select(h => new[]
                {
                    h.Name, h.Cluster ?? string.Empty, h.ConnectionState, h.InMaintenance ? "yes" : "no", h.Model ?? string.Empty,
                    P(h.CpuUsageMhz, h.CpuTotalMhz), P(h.MemUsageBytes, h.MemTotalBytes),
                    vms.Count(v => v.SourceDeviceId == h.SourceDeviceId && v.HostMoRef == h.MoRef).ToString(Inv), h.OverallStatus ?? string.Empty,
                }).ToList(),
                new HashSet<int> { 5, 6, 7 }, "No ESXi hosts (add a vCenter device with a vCenter credential)."),
        });

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Datastore capacity",
            Bars = datastores.Count == 0 ? null : new ReportBars(datastores.OrderByDescending(d => d.CapacityBytes <= 0 ? 0 : 1.0 * (d.CapacityBytes - d.FreeBytes) / d.CapacityBytes).Take(15)
                .Select(d => (d.Name, d.CapacityBytes <= 0 ? 0 : 100.0 * (d.CapacityBytes - d.FreeBytes) / d.CapacityBytes, Pct(d.CapacityBytes <= 0 ? 0 : 100.0 * (d.CapacityBytes - d.FreeBytes) / d.CapacityBytes, 1))).ToList(), 100),
            Table = new ReportTable(["Datastore", "Type", "Capacity", "Free", "Used", "Provisioned", "Hosts", "Accessible"],
                datastores.Select(d => new[]
                {
                    d.Name, d.Type ?? string.Empty, Bytes(d.CapacityBytes), Bytes(d.FreeBytes), P(d.CapacityBytes - d.FreeBytes, d.CapacityBytes),
                    d.UncommittedBytes is { } u ? P(d.CapacityBytes - d.FreeBytes + u, d.CapacityBytes) : "–", d.HostCount.ToString(Inv), d.Accessible ? "yes" : "NO",
                }).ToList(),
                new HashSet<int> { 2, 3, 4, 5, 6 }, "No datastores."),
        });

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Snapshots (oldest first)",
            Table = new ReportTable(["VM", "Snapshot", "Created (PKT)", "Age (days)", "Size"],
                snapshots.Select(s => new[] { s.VmName, s.Name, Local(s.CreatedUtc), ((now - s.CreatedUtc).TotalDays).ToString("0", Inv), s.SizeBytes is { } b ? Bytes(b) : "–" }).ToList(),
                new HashSet<int> { 3, 4 }, "No snapshots."),
        });

        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Powered-off and suspended VMs",
            Table = new ReportTable(["VM", "State", "Host", "Guest OS", "vCPU", "Memory", "Datastores"],
                off.Select(v => new[]
                {
                    v.Name, v.PowerState, v.HostName ?? string.Empty, v.GuestOs ?? string.Empty, v.NumCpu?.ToString(Inv) ?? string.Empty,
                    v.MemoryMb is { } m ? $"{m / 1024.0:0.#} GB" : string.Empty, v.Datastores ?? string.Empty,
                }).ToList(),
                new HashSet<int> { 4, 5 }, "All VMs are powered on."),
        });
    }

    // ------------------------------------------------------------------ inventory

    private async Task InventoryAsync(Context c, CancellationToken ct)
    {
        var servers = await _db.ServerInfos.AsNoTracking().ToDictionaryAsync(s => s.DeviceId, ct);
        var summary = new ReportSection { Heading = "Summary" };
        summary.Summary.Add(("Devices", c.Devices.Count.ToString(Inv)));
        foreach (var t in c.Devices.GroupBy(d => d.Type).OrderByDescending(g => g.Count()).Take(7))
        {
            summary.Summary.Add((t.Key.ToString(), t.Count().ToString(Inv)));
        }

        c.Doc.Sections.Add(summary);
        c.Doc.Sections.Add(new ReportSection
        {
            Heading = "Devices",
            Table = new ReportTable(
                ["Device", "Type", "IP address", "Group", "Vendor", "Model", "Serial number", "Firmware / OS", "Status"],
                c.Devices.Select(d => new[]
                {
                    d.Name, d.Type.ToString(), d.IpAddress, c.Group(d), d.Vendor ?? string.Empty, d.Model ?? string.Empty, d.SerialNumber ?? string.Empty,
                    servers.TryGetValue(d.Id, out var s) && s.OsName is { } os ? os : d.Firmware ?? string.Empty,
                    d.MaintenanceMode ? "Maintenance" : d.Status.ToString(),
                }).ToList()),
        });
    }

    // ------------------------------------------------------------------ helpers

    private static int Column(SeriesData data, string name)
    {
        for (var i = 0; i < data.Columns.Count; i++)
        {
            if (data.Columns[i] == name)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Column {name} missing.");
    }

    private static List<double> Values(SeriesData data, int col, bool max) =>
        data.Points.Select(p => max && p.Max is { } m ? m[col] : p.Avg[col]).Where(v => v.HasValue).Select(v => v!.Value).ToList();

    public static string Local(DateTime utc) => LocalTime.ToLocal(utc).ToString("yyyy-MM-dd HH:mm", Inv);

    public static string Pct(double v, int digits) => v.ToString(digits switch { 0 => "0", 1 => "0.0", _ => "0.000" }, Inv) + " %";

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h"
            : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
            : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min"
            : $"{(int)t.TotalSeconds} s";
    }

    public static string Bytes(long b) => b switch
    {
        >= 1L << 40 => (b / (double)(1L << 40)).ToString("0.0", Inv) + " TB",
        >= 1L << 30 => (b / (double)(1L << 30)).ToString("0.0", Inv) + " GB",
        >= 1L << 20 => (b / (double)(1L << 20)).ToString("0.0", Inv) + " MB",
        _ => b.ToString(Inv) + " B",
    };

    private static string Speed(long bps) => bps switch
    {
        >= 1_000_000_000 => (bps / 1e9).ToString("0.#", Inv) + " Gb/s",
        >= 1_000_000 => (bps / 1e6).ToString("0.#", Inv) + " Mb/s",
        0 => "–",
        _ => (bps / 1e3).ToString("0.#", Inv) + " kb/s",
    };
}
