using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Alerts.Engine;
using NetWatch.Alerts.Notifications;
using NetWatch.Collectors.Receivers;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Live;
using NetWatch.Collectors.Scheduling;
using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Data.Metrics;
using NetWatch.Web.Auth;

namespace NetWatch.Web.Api;

public sealed record StatusCounts(int Total, int Up, int Warning, int Down, int Unknown, int Maintenance);

public sealed record TypeSummary(DeviceType Type, StatusCounts Counts);

public sealed record GroupHealth(int GroupId, string Name, string Path, StatusCounts Counts);

public sealed record TopInterface(int InterfaceId, int DeviceId, string DeviceName, string InterfaceName, long SpeedBps, double? InBps, double? OutBps, double? InUtilPct, double? OutUtilPct);

public sealed record TopDevice(int DeviceId, string Name, DeviceType Type, double Value);

public sealed record TopDisk(int DeviceId, string DeviceName, string Volume, double UsedPct, long FreeBytes, long TotalBytes);

/// <summary>Overview dashboard data and service self-monitoring.</summary>
public static class OverviewEndpoints
{
    public static void MapOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/overview", OverviewAsync).RequireAuthorization(Policies.Viewer);
        app.MapGet("/api/system/status", SystemStatusAsync).RequireAuthorization(Policies.Admin);
        app.MapGet("/api/system/info", (SettingsService s) => Info(s)).RequireAuthorization(Policies.Viewer);
        app.MapGet("/api/topology", TopologyAsync).RequireAuthorization(Policies.Viewer);
    }

    internal static StatusCounts Count(IEnumerable<DeviceStatus> statuses)
    {
        int up = 0, warn = 0, down = 0, unknown = 0, maint = 0, total = 0;
        foreach (var s in statuses)
        {
            total++;
            switch (s)
            {
                case DeviceStatus.Up: up++; break;
                case DeviceStatus.Warning: warn++; break;
                case DeviceStatus.Down: down++; break;
                case DeviceStatus.Maintenance: maint++; break;
                default: unknown++; break;
            }
        }

        return new StatusCounts(total, up, warn, down, unknown, maint);
    }

    private static async Task<IResult> OverviewAsync(NetWatchDbContext db, EventsDbContext events, LiveStateCache live, IActiveAlertIndex alerts, InventoryCache inventory, IDependencyStatus dependencies, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Include(d => d.Tags).ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().ToListAsync(ct);
        var paths = DeviceStatusHelper.GroupPaths(groups);
        var now = DateTime.UtcNow;
        var items = devices.Where(d => d.IsEnabled).Select(d => DeviceEndpoints.ToListItem(d, paths, live, alerts, inventory, dependencies, now)).ToList();
        var byId = items.ToDictionary(i => i.Id);

        var byType = items.GroupBy(i => i.Type).OrderBy(g => g.Key)
            .Select(g => new TypeSummary(g.Key, Count(g.Select(i => i.Status)))).ToList();

        // Health tiles for top-level groups and their direct children.
        var tiles = new List<GroupHealth>();
        foreach (var g in groups.Where(g => g.ParentId is null || groups.Any(p => p.Id == g.ParentId && p.ParentId is null)).OrderBy(g => g.SortOrder).ThenBy(g => g.Name))
        {
            var subtree = DeviceEndpoints.Subtree(groups, g.Id);
            var members = items.Where(i => i.GroupId is { } gid && subtree.Contains(gid)).ToList();
            if (members.Count > 0)
            {
                tiles.Add(new GroupHealth(g.Id, g.Name, paths.GetValueOrDefault(g.Id, g.Name), Count(members.Select(m => m.Status))));
            }
        }

        var names = devices.ToDictionary(d => d.Id, d => d.Name);
        var topInterfaces = live.TopInterfaces(10)
            .Where(i => names.ContainsKey(i.DeviceId))
            .Select(i => new TopInterface(i.InterfaceId, i.DeviceId, names[i.DeviceId], i.Name, i.SpeedBps, i.InBps, i.OutBps, i.InUtilPct, i.OutUtilPct))
            .ToList();
        var liveDevices = live.Devices.Where(d => byId.ContainsKey(d.DeviceId)).ToList();
        var topCpu = liveDevices.Where(d => d.CpuPct.HasValue).OrderByDescending(d => d.CpuPct).Take(10)
            .Select(d => new TopDevice(d.DeviceId, byId[d.DeviceId].Name, byId[d.DeviceId].Type, d.CpuPct!.Value)).ToList();
        var topMemory = liveDevices.Where(d => d.MemPct.HasValue).OrderByDescending(d => d.MemPct).Take(10)
            .Select(d => new TopDevice(d.DeviceId, byId[d.DeviceId].Name, byId[d.DeviceId].Type, d.MemPct!.Value)).ToList();

        var recent = await events.DeviceEvents.AsNoTracking().OrderByDescending(e => e.TimestampUtc).Take(15).ToListAsync(ct);
        var alertCounts = alerts.CountBySeverity();

        // Fullest disks across all servers.
        var topDisks = (await db.Volumes.AsNoTracking().Where(v => v.TotalBytes > 0).ToListAsync(ct))
            .Where(v => byId.ContainsKey(v.DeviceId))
            .OrderByDescending(v => v.UsedPct)
            .Take(10)
            .Select(v => new TopDisk(v.DeviceId, byId[v.DeviceId].Name, v.Name, v.UsedPct, v.FreeBytes, v.TotalBytes))
            .ToList();
        var hourAgo = now.AddHours(-1);
        var syslogLastHour = await events.SyslogMessages.AsNoTracking().CountAsync(m => m.ReceivedUtc >= hourAgo, ct);
        var syslogErrorsLastHour = await events.SyslogMessages.AsNoTracking().CountAsync(m => m.ReceivedUtc >= hourAgo && m.SyslogSeverity <= 3, ct);
        var trapsLastHour = await events.SnmpTraps.AsNoTracking().CountAsync(t => t.ReceivedUtc >= hourAgo, ct);
        var maintenance = items.Count(i => i.MaintenanceMode);

        return Results.Ok(new
        {
            totals = Count(items.Select(i => i.Status)),
            byType,
            alerts = Enum.GetValues<Severity>().ToDictionary(s => s.ToString(), s => alertCounts.GetValueOrDefault(s)),
            groups = tiles,
            topInterfaces,
            topCpu,
            topMemory,
            topDisks,
            received = new { syslogLastHour, syslogErrorsLastHour, trapsLastHour },
            inMaintenance = maintenance,
            problemDevices = items.Where(i => i.Status is DeviceStatus.Down or DeviceStatus.Warning)
                .OrderByDescending(i => i.Status == DeviceStatus.Down).ThenByDescending(i => i.WorstAlert).Take(20).ToList(),
            recentEvents = recent.Select(e => new DeviceEventDto(e.Id, e.TimestampUtc, e.DeviceId, e.DeviceId is { } d ? names.GetValueOrDefault(d) : null, e.Kind, e.Severity, e.Message)),
        });
    }

    public sealed record TopologyNode(int Id, string Name, string IpAddress, DeviceType Type, DeviceStatus Status, Severity? WorstAlert, int? ParentId, string? GroupPath, string? UnreachableVia);

    /// <summary>Devices with their upstream (parent) device, for the topology map.</summary>
    private static async Task<IResult> TopologyAsync(NetWatchDbContext db, LiveStateCache live, IActiveAlertIndex alerts, InventoryCache inventory, IDependencyStatus dependencies, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Include(d => d.Tags).Where(d => d.IsEnabled).ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().ToListAsync(ct);
        var paths = DeviceStatusHelper.GroupPaths(groups);
        var now = DateTime.UtcNow;
        var ids = devices.Select(d => d.Id).ToHashSet();
        return Results.Ok(devices.Select(d =>
        {
            var item = DeviceEndpoints.ToListItem(d, paths, live, alerts, inventory, dependencies, now);
            return new TopologyNode(d.Id, d.Name, d.IpAddress, d.Type, item.Status, item.WorstAlert,
                d.ParentDeviceId is { } p && ids.Contains(p) && p != d.Id ? p : null, item.GroupPath, item.UnreachableVia);
        }));
    }

    private static IResult SystemStatusAsync(
        CollectorStats stats,
        SqliteMetricStore metrics,
        AlertManager alerts,
        InventoryCache inventory,
        LiveStateCache live,
        NetWatchPaths paths,
        ReceiverStats receivers,
        EventIngestService ingest,
        NotificationDispatcher notifications,
        Infrastructure.SelfMonitorService selfMonitor,
        Data.Backup.BackupService backups)
    {
        long FileSize(string path)
        {
            long size = 0;
            foreach (var f in new[] { path, path + "-wal" })
            {
                size += File.Exists(f) ? new FileInfo(f).Length : 0;
            }

            return size;
        }

        var drive = new DriveInfo(Path.GetPathRoot(paths.Root) ?? "C:\\");
        return Results.Ok(new
        {
            version = Version(),
            startedUtc = stats.StartedUtc,
            devices = inventory.All.Count,
            interfacesLive = live.InterfaceCount,
            polling = new
            {
                stats.TotalPolls,
                stats.FailedPolls,
                stats.Overruns,
                stats.InFlight,
                stats.Queued,
                avgDurationMs = Math.Round(stats.AvgDurationMs, 1),
                maxDurationMs = Math.Round(stats.MaxDurationMs, 1),
            },
            metrics = new { pendingWrites = metrics.PendingWrites, droppedSamples = metrics.DroppedSamples },
            alertEngine = new
            {
                queued = alerts.QueuedSignals,
                processed = alerts.ProcessedSignals,
                dropped = alerts.DroppedSignals,
                suppressed = alerts.SuppressedConditions.Count,
                suppressedByMaintenance = alerts.SuppressedConditions.Count(s => s.Reason == "maintenance"),
            },
            receivers = new
            {
                syslogReceived = receivers.SyslogReceived,
                trapsReceived = receivers.TrapsReceived,
                stored = receivers.Stored,
                dropped = receivers.Dropped,
                unknownSource = receivers.UnknownSource,
                parseErrors = receivers.ParseErrors,
                queued = ingest.Queued,
                listeners = receivers.ListenerSnapshot(),
            },
            notifications = notifications.Status,
            selfChecks = selfMonitor.Latest,
            selfChecksUtc = selfMonitor.LastRunUtc,
            selfDeviceId = selfMonitor.SelfDeviceId,
            backup = new { lastAttemptUtc = backups.LastAttemptUtc, lastError = backups.LastError, running = backups.Running, folder = backups.TargetFolder },
            storage = new
            {
                configDbBytes = FileSize(paths.ConfigDb),
                metricsDbBytes = FileSize(paths.MetricsDb),
                eventsDbBytes = FileSize(paths.EventsDb),
                diskFreeBytes = drive.IsReady ? drive.AvailableFreeSpace : 0,
                diskTotalBytes = drive.IsReady ? drive.TotalSize : 0,
                dataPath = paths.Root,
            },
        });
    }

    private static IResult Info(SettingsService settings) =>
        Results.Ok(new { product = "NetWatch", version = Version(), timeZone = "Asia/Karachi", repeatSoundEveryMin = settings.Alerting.RepeatSoundEveryMin });

    private static string Version() =>
        typeof(OverviewEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
}
