using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Api;

public sealed record VcenterSummary(int DeviceId, string Name, string IpAddress, DateTime? LastPollUtc, int Hosts, int HostsConnected, int Vms, int VmsPoweredOn, int Datastores, int Snapshots, int Alarms);

public sealed record VmwareHostDto(int Id, int SourceDeviceId, string MoRef, string Name, string? Cluster, string ConnectionState, bool InMaintenance, string? OverallStatus,
    string? Vendor, string? Model, string? Product, int? CpuCores, double? CpuPct, double? MemPct, long? MemTotalBytes, DateTime? BootTimeUtc,
    int SensorWarnings, int SensorAlarms, int? DeviceId, int VmCount, int VmsPoweredOn, DateTime LastSeenUtc);

public sealed record VmwareVmDto(int Id, int SourceDeviceId, string MoRef, string Name, string? HostName, string PowerState, string? GuestOs, string? IpAddress,
    string? ToolsRunningStatus, string? ToolsVersionStatus, string? OverallStatus, int? NumCpu, int? MemoryMb, double? CpuPct, double? CpuReadyPct, double? MemPct,
    long? BalloonedMb, long? CommittedBytes, long? UncommittedBytes, double? GuestDiskMaxPct, string? Datastores, int SnapshotCount, DateTime? BootTimeUtc,
    int? DeviceId, DateTime LastSeenUtc);

public sealed record VmwareDatastoreDto(int Id, int SourceDeviceId, string MoRef, string Name, string? Type, long CapacityBytes, long FreeBytes, double UsedPct,
    long? UncommittedBytes, bool Accessible, string? MaintenanceMode, int HostCount, int VmCount, DateTime LastSeenUtc);

public sealed record VmwareSnapshotDto(long Id, int SourceDeviceId, string VmMoRef, string VmName, int? VmDeviceId, string Name, string? Description, DateTime CreatedUtc,
    double AgeDays, long? SizeBytes, bool IsCurrent);

public sealed record VmwareAlarmDto(long Id, int SourceDeviceId, string AlarmName, string EntityMoRef, string? EntityName, string Status, bool Acknowledged, DateTime TriggeredUtc);

public sealed record VmwareEventDto(long Id, int SourceDeviceId, string? SourceName, long EventKey, DateTime TimestampUtc, string EventType, Severity Severity, string? UserName, string? EntityName, string Message);

/// <summary>VMware inventory and health collected from vCenter / ESXi.</summary>
public static class VmwareEndpoints
{
    public static void MapVmwareEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/vmware").RequireAuthorization(Policies.Viewer);
        read.MapGet("/summary", SummaryAsync);
        read.MapGet("/hosts", HostsAsync);
        read.MapGet("/vms", VmsAsync);
        read.MapGet("/datastores", DatastoresAsync);
        read.MapGet("/snapshots", SnapshotsAsync);
        read.MapGet("/alarms", AlarmsAsync);
        read.MapGet("/events", EventsAsync);
        read.MapGet("/vm-by-device/{deviceId:int}", VmByDeviceAsync);

        app.MapPost("/api/devices/{id:int}/tls-thumbprint/reset", ResetThumbprintAsync).RequireAuthorization(Policies.Admin);
    }

    private static async Task<IResult> SummaryAsync(NetWatchDbContext db, CancellationToken ct)
    {
        var sources = await db.Devices.AsNoTracking()
            .Where(d => d.Credentials.Any(c => c.Purpose == CredentialPurpose.VCenter))
            .Select(d => new { d.Id, d.Name, d.IpAddress, d.LastPollUtc })
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
        var hosts = await db.VmwareHosts.AsNoTracking().GroupBy(h => h.SourceDeviceId)
            .Select(g => new { g.Key, Total = g.Count(), Connected = g.Count(h => h.ConnectionState == "connected") }).ToDictionaryAsync(x => x.Key, ct);
        var vms = await db.VmwareVms.AsNoTracking().GroupBy(v => v.SourceDeviceId)
            .Select(g => new { g.Key, Total = g.Count(), On = g.Count(v => v.PowerState == "poweredOn") }).ToDictionaryAsync(x => x.Key, ct);
        var ds = await db.VmwareDatastores.AsNoTracking().GroupBy(d => d.SourceDeviceId).Select(g => new { g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        var snaps = await db.VmwareSnapshots.AsNoTracking().GroupBy(s => s.SourceDeviceId).Select(g => new { g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        var alarms = await db.VmwareAlarms.AsNoTracking().GroupBy(a => a.SourceDeviceId).Select(g => new { g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        return Results.Ok(sources.Select(s => new VcenterSummary(
            s.Id, s.Name, s.IpAddress, s.LastPollUtc,
            hosts.GetValueOrDefault(s.Id)?.Total ?? 0, hosts.GetValueOrDefault(s.Id)?.Connected ?? 0,
            vms.GetValueOrDefault(s.Id)?.Total ?? 0, vms.GetValueOrDefault(s.Id)?.On ?? 0,
            ds.GetValueOrDefault(s.Id), snaps.GetValueOrDefault(s.Id), alarms.GetValueOrDefault(s.Id))));
    }

    private static async Task<IResult> HostsAsync(NetWatchDbContext db, int? sourceId, CancellationToken ct)
    {
        var rows = await db.VmwareHosts.AsNoTracking().Where(h => sourceId == null || h.SourceDeviceId == sourceId).OrderBy(h => h.Cluster).ThenBy(h => h.Name).ToListAsync(ct);
        var vmCounts = await db.VmwareVms.AsNoTracking().Where(v => sourceId == null || v.SourceDeviceId == sourceId)
            .GroupBy(v => new { v.SourceDeviceId, v.HostMoRef })
            .Select(g => new { g.Key.SourceDeviceId, g.Key.HostMoRef, Total = g.Count(), On = g.Count(v => v.PowerState == "poweredOn") })
            .ToListAsync(ct);
        return Results.Ok(rows.Select(h =>
        {
            var c = vmCounts.FirstOrDefault(x => x.SourceDeviceId == h.SourceDeviceId && x.HostMoRef == h.MoRef);
            double? cpu = h.CpuTotalMhz is > 0 && h.CpuUsageMhz is { } u ? Math.Round(100.0 * u / h.CpuTotalMhz.Value, 1) : null;
            double? mem = h.MemTotalBytes is > 0 && h.MemUsageBytes is { } m ? Math.Round(100.0 * m / h.MemTotalBytes.Value, 1) : null;
            return new VmwareHostDto(h.Id, h.SourceDeviceId, h.MoRef, h.Name, h.Cluster, h.ConnectionState, h.InMaintenance, h.OverallStatus, h.Vendor, h.Model, h.Product,
                h.CpuCores, cpu, mem, h.MemTotalBytes, h.BootTimeUtc, h.SensorWarnings, h.SensorAlarms, h.DeviceId, c?.Total ?? 0, c?.On ?? 0, h.LastSeenUtc);
        }).ToList());
    }

    private static VmwareVmDto ToDto(Core.Entities.VmwareVm v) => new(v.Id, v.SourceDeviceId, v.MoRef, v.Name, v.HostName, v.PowerState, v.GuestOs, v.IpAddress,
        v.ToolsRunningStatus, v.ToolsVersionStatus, v.OverallStatus, v.NumCpu, v.MemoryMb, v.CpuPct, v.CpuReadyPct, v.MemPct, v.BalloonedMb, v.CommittedBytes,
        v.UncommittedBytes, v.GuestDiskMaxPct, v.Datastores, v.SnapshotCount, v.BootTimeUtc, v.DeviceId, v.LastSeenUtc);

    private static async Task<IResult> VmsAsync(NetWatchDbContext db, int? sourceId, string? host, CancellationToken ct)
    {
        var q = db.VmwareVms.AsNoTracking().Where(v => sourceId == null || v.SourceDeviceId == sourceId);
        if (!string.IsNullOrWhiteSpace(host))
        {
            q = q.Where(v => v.HostMoRef == host);
        }

        return Results.Ok((await q.OrderBy(v => v.Name).ToListAsync(ct)).Select(ToDto).ToList());
    }

    private static async Task<IResult> VmByDeviceAsync(int deviceId, NetWatchDbContext db, CancellationToken ct)
    {
        var vm = await db.VmwareVms.AsNoTracking().FirstOrDefaultAsync(v => v.DeviceId == deviceId, ct);
        if (vm is null)
        {
            return Results.NoContent();
        }

        var snapshots = await db.VmwareSnapshots.AsNoTracking().Where(s => s.SourceDeviceId == vm.SourceDeviceId && s.VmMoRef == vm.MoRef).OrderBy(s => s.CreatedUtc).ToListAsync(ct);
        var now = DateTime.UtcNow;
        return Results.Ok(new
        {
            vm = ToDto(vm),
            snapshots = snapshots.Select(s => new VmwareSnapshotDto(s.Id, s.SourceDeviceId, s.VmMoRef, s.VmName, deviceId, s.Name, s.Description, s.CreatedUtc,
                Math.Round((now - s.CreatedUtc).TotalDays, 1), s.SizeBytes, s.IsCurrent)),
        });
    }

    private static async Task<IResult> DatastoresAsync(NetWatchDbContext db, int? sourceId, CancellationToken ct) =>
        Results.Ok((await db.VmwareDatastores.AsNoTracking().Where(d => sourceId == null || d.SourceDeviceId == sourceId).OrderBy(d => d.Name).ToListAsync(ct))
            .Select(d => new VmwareDatastoreDto(d.Id, d.SourceDeviceId, d.MoRef, d.Name, d.Type, d.CapacityBytes, d.FreeBytes, d.UsedPct, d.UncommittedBytes, d.Accessible,
                d.MaintenanceMode, d.HostCount, d.VmCount, d.LastSeenUtc))
            .ToList());

    private static async Task<IResult> SnapshotsAsync(NetWatchDbContext db, int? sourceId, CancellationToken ct)
    {
        var rows = await db.VmwareSnapshots.AsNoTracking().Where(s => sourceId == null || s.SourceDeviceId == sourceId).OrderBy(s => s.CreatedUtc).ToListAsync(ct);
        var devices = await db.VmwareVms.AsNoTracking().Where(v => v.DeviceId != null).Select(v => new { v.SourceDeviceId, v.MoRef, v.DeviceId }).ToListAsync(ct);
        var now = DateTime.UtcNow;
        return Results.Ok(rows.Select(s => new VmwareSnapshotDto(s.Id, s.SourceDeviceId, s.VmMoRef, s.VmName,
            devices.FirstOrDefault(d => d.SourceDeviceId == s.SourceDeviceId && d.MoRef == s.VmMoRef)?.DeviceId,
            s.Name, s.Description, s.CreatedUtc, Math.Round((now - s.CreatedUtc).TotalDays, 1), s.SizeBytes, s.IsCurrent)).ToList());
    }

    private static async Task<IResult> AlarmsAsync(NetWatchDbContext db, int? sourceId, CancellationToken ct) =>
        Results.Ok(await db.VmwareAlarms.AsNoTracking().Where(a => sourceId == null || a.SourceDeviceId == sourceId).OrderByDescending(a => a.TriggeredUtc)
            .Select(a => new VmwareAlarmDto(a.Id, a.SourceDeviceId, a.AlarmName, a.EntityMoRef, a.EntityName, a.Status, a.Acknowledged, a.TriggeredUtc))
            .ToListAsync(ct));

    private static async Task<IResult> EventsAsync(EventsDbContext events, NetWatchDbContext db, int? sourceId, Severity? minSeverity, string? search, DateTime? from, DateTime? to, int? page, int? pageSize, CancellationToken ct)
    {
        var q = events.VmwareEvents.AsNoTracking().AsQueryable();
        if (sourceId is { } sid)
        {
            q = q.Where(e => e.SourceDeviceId == sid);
        }

        if (minSeverity is { } min)
        {
            var allowed = Enum.GetValues<Severity>().Where(s => s >= min).ToList();
            q = q.Where(e => allowed.Contains(e.Severity));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            q = q.Where(e => EF.Functions.Like(e.Message, term) || EF.Functions.Like(e.EventType, term) || EF.Functions.Like(e.EntityName ?? string.Empty, term));
        }

        if (from is { } f)
        {
            var fu = f.ToUniversalTime();
            q = q.Where(e => e.TimestampUtc >= fu);
        }

        if (to is { } t)
        {
            var tu = t.ToUniversalTime();
            q = q.Where(e => e.TimestampUtc < tu);
        }

        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 200, 1, 2000);
        var total = await q.Take(10_001).CountAsync(ct);
        var rows = await q.OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(r => r.SourceDeviceId).Distinct().ToList();
        var names = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var items = rows.Select(e => new VmwareEventDto(e.Id, e.SourceDeviceId, names.GetValueOrDefault(e.SourceDeviceId), e.EventKey, e.TimestampUtc, e.EventType, e.Severity,
            e.UserName, e.EntityName, e.Message)).ToList();
        return Results.Ok(new { items, total, page = p, pageSize = size, totalCapped = total > 10_000 });
    }

    private static async Task<IResult> ResetThumbprintAsync(int id, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var count = await db.Devices.Where(d => d.Id == id).ExecuteUpdateAsync(u => u.SetProperty(d => d.TlsThumbprint, (string?)null), ct);
        if (count == 0)
        {
            return Results.NotFound();
        }

        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "TlsThumbprintCleared", "Device", id.Inv(), "The next vCenter connection pins the presented certificate.");
        return Results.NoContent();
    }
}
