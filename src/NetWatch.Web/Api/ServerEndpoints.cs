using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Live;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Api;

public static partial class WatchedServiceNames
{
    [GeneratedRegex(@"^[A-Za-z0-9 _.$@:\-]{1,200}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string name) => Pattern().IsMatch(name);
}

public sealed record VolumeDto(int Id, string Name, string? Label, string? FileSystem, long TotalBytes, long FreeBytes, double UsedPct, DateTime LastSeenUtc);

public sealed record WatchedServiceDto(int Id, string ServiceName, string? DisplayName, ServiceState State, string? StartMode, DateTime? LastSeenUtc, DateTime? LastChangeUtc);

public sealed record ServerInfoDto(
    ServerCollectionMethod Method,
    string? OsName,
    string? OsVersion,
    DateTime? LastBootUtc,
    bool? PendingReboot,
    DateTime? LastPatchUtc,
    string? LastPatchId,
    int? CpuCount,
    long? TotalMemoryBytes,
    double? Load1,
    double? Load5,
    double? Load15,
    DateTime? LastSuccessUtc,
    DateTime LastAttemptUtc,
    string? LastError,
    string? EventLogError);

public sealed record ServerDetailDto(ServerInfoDto? Info, IReadOnlyList<VolumeDto> Volumes, IReadOnlyList<WatchedServiceDto> Services,
    IReadOnlyList<GpuDto>? Gpus = null, IReadOnlyList<HardwareComponentDto>? Hardware = null);

public sealed record GpuDto(int Index, string Name, string? Vendor, string? DriverVersion, double? UtilizationPct, double? MemoryUsedMb, double? MemoryTotalMb,
    double? TemperatureC, double? PowerW, double? FanPct, string? Problem, DateTime LastSeenUtc);

public sealed record HardwareComponentDto(string Source, string Category, string Name, string? Health, string? State, double? Reading, string? Unit, string? Details, DateTime LastSeenUtc);

public sealed record ServerListItem(
    int Id,
    string Name,
    string IpAddress,
    DeviceType Type,
    OsKind Os,
    DeviceStatus Status,
    Severity? WorstAlert,
    string? GroupPath,
    double? CpuPct,
    double? MemPct,
    double? WorstDiskPct,
    string? WorstDisk,
    int ServicesTotal,
    int ServicesRunning,
    ServerCollectionMethod? Method,
    string? OsName,
    DateTime? LastBootUtc,
    bool? PendingReboot,
    DateTime? LastPatchUtc,
    double? Load5,
    int? CpuCount,
    DateTime? LastSuccessUtc,
    string? LastError,
    bool HasManagementCredential);

public sealed record SeriesInfoDto(int Id, string Key, string Instance, string DisplayName);

public sealed record WindowsEventDto(long Id, DateTime TimestampUtc, int DeviceId, string? DeviceName, string LogName, string? Provider, int EventId, int Level, string Message);

public sealed record ServicesUpdate(IReadOnlyList<string>? Services);

/// <summary>Server monitoring: server list, per-server facts (volumes, services), generic series, Windows events.</summary>
public static class ServerEndpoints
{
    public static void MapServerEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api").RequireAuthorization(Policies.Viewer);
        read.MapGet("/servers", ListAsync);
        read.MapGet("/devices/{id:int}/server", DetailAsync);
        read.MapGet("/devices/{id:int}/series", (int id, IMetricStore metrics) =>
            Results.Ok(metrics.ListSeries(id).Select(s => new SeriesInfoDto(s.Id, s.Key, s.Instance, MetricKeys.DisplayNames.GetValueOrDefault(s.Key, s.Key)))));
        read.MapGet("/series/{id:int}/metrics", SeriesMetricsAsync);

        var admin = app.MapGroup("/api").RequireAuthorization(Policies.Admin);
        admin.MapPut("/devices/{id:int}/services", UpdateServicesAsync);
        admin.MapPost("/devices/{id:int}/ssh-host-key/reset", ResetHostKeyAsync);
        admin.MapPost("/devices/{id:int}/bmc-thumbprint/reset", async (int id, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct) =>
        {
            var n = await db.Devices.Where(d => d.Id == id).ExecuteUpdateAsync(u => u.SetProperty(d => d.BmcTlsThumbprint, (string?)null), ct);
            if (n == 0)
            {
                return Results.NotFound();
            }

            notifier.NotifyInventoryChanged();
            await audit.WriteAsync(http.Actor(), "BmcCertificateReset", "Device", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Results.NoContent();
        });
    }

    private static async Task<IResult> ListAsync(NetWatchDbContext db, LiveStateCache live, IActiveAlertIndex alerts, InventoryCache inventory, CancellationToken ct)
    {
        var serverTypes = new[] { DeviceType.Server, DeviceType.VM, DeviceType.DomainController, DeviceType.Hypervisor };
        var devices = await db.Devices.AsNoTracking()
            .Include(d => d.Credentials)
            .Where(d => serverTypes.Contains(d.Type))
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
        var ids = devices.Select(d => d.Id).ToList();
        var infos = await db.ServerInfos.AsNoTracking().Where(i => ids.Contains(i.DeviceId)).ToDictionaryAsync(i => i.DeviceId, ct);
        var volumes = (await db.Volumes.AsNoTracking().Where(v => ids.Contains(v.DeviceId)).ToListAsync(ct))
            .GroupBy(v => v.DeviceId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.UsedPct).First());
        var services = (await db.WatchedServices.AsNoTracking().Where(s => ids.Contains(s.DeviceId)).ToListAsync(ct))
            .GroupBy(s => s.DeviceId).ToDictionary(g => g.Key, g => (Total: g.Count(), Running: g.Count(s => s.LastState == ServiceState.Running)));
        var paths = DeviceStatusHelper.GroupPaths(await db.Groups.AsNoTracking().ToListAsync(ct));
        var now = DateTime.UtcNow;

        var items = devices.Select(d =>
        {
            var l = live.GetDevice(d.Id);
            var worst = alerts.GetWorstSeverity(d.Id);
            var inMaintenance = inventory.GetSnapshot(d.Id)?.InMaintenance ?? DeviceStatusHelper.InMaintenance(d, now);
            var info = infos.GetValueOrDefault(d.Id);
            var disk = volumes.GetValueOrDefault(d.Id);
            var svc = services.GetValueOrDefault(d.Id);
            return new ServerListItem(
                d.Id, d.Name, d.IpAddress, d.Type, d.Os,
                DeviceStatusHelper.Effective(l?.Status ?? d.Status, d.IsEnabled, inMaintenance, worst), worst,
                d.GroupId is { } g ? paths.GetValueOrDefault(g) : null,
                l?.CpuPct, l?.MemPct, disk?.UsedPct, disk?.Name,
                svc.Total, svc.Running,
                info?.Method, info?.OsName, info?.LastBootUtc, info?.PendingReboot, info?.LastPatchUtc, info?.Load5, info?.CpuCount,
                info?.LastSuccessUtc, info?.LastError,
                d.Credentials.Any(c => c.Purpose == CredentialPurpose.Management));
        }).ToList();
        return Results.Ok(items);
    }

    private static async Task<IResult> DetailAsync(int id, NetWatchDbContext db, CancellationToken ct)
    {
        var info = await db.ServerInfos.AsNoTracking().FirstOrDefaultAsync(i => i.DeviceId == id, ct);
        var volumes = await db.Volumes.AsNoTracking().Where(v => v.DeviceId == id).OrderBy(v => v.Name).ToListAsync(ct);
        var services = await db.WatchedServices.AsNoTracking().Where(s => s.DeviceId == id).OrderBy(s => s.ServiceName).ToListAsync(ct);
        var gpus = await db.ServerGpus.AsNoTracking().Where(g => g.DeviceId == id).OrderBy(g => g.Index).ToListAsync(ct);
        var hardware = await db.HardwareComponents.AsNoTracking().Where(c => c.DeviceId == id).OrderBy(c => c.Category).ThenBy(c => c.Name).ToListAsync(ct);
        return Results.Ok(new ServerDetailDto(
            info is null ? null : new ServerInfoDto(info.Method, info.OsName, info.OsVersion, info.LastBootUtc, info.PendingReboot, info.LastPatchUtc, info.LastPatchId,
                info.CpuCount, info.TotalMemoryBytes, info.Load1, info.Load5, info.Load15, info.LastSuccessUtc, info.LastAttemptUtc, info.LastError, info.EventLogError),
            volumes.Select(v => new VolumeDto(v.Id, v.Name, v.Label, v.FileSystem, v.TotalBytes, v.FreeBytes, v.UsedPct, v.LastSeenUtc)).ToList(),
            services.Select(s => new WatchedServiceDto(s.Id, s.ServiceName, s.DisplayName, s.LastState, s.StartMode, s.LastSeenUtc, s.LastChangeUtc)).ToList(),
            gpus.Select(g => new GpuDto(g.Index, g.Name, g.Vendor, g.DriverVersion, g.UtilizationPct, g.MemoryUsedMb, g.MemoryTotalMb, g.TemperatureC, g.PowerW, g.FanPct, g.Problem, g.LastSeenUtc)).ToList(),
            hardware.Select(c => new HardwareComponentDto(c.Source, c.Category, c.Name, c.Health, c.State, c.Reading, c.Unit, c.Details, c.LastSeenUtc)).ToList()));
    }

    private static async Task<IResult> SeriesMetricsAsync(int id, DateTime? from, DateTime? to, IMetricStore metrics, CancellationToken ct)
    {
        var t = (to ?? DateTime.UtcNow).ToUniversalTime();
        var f = (from ?? t.AddHours(-1)).ToUniversalTime();
        if (f >= t)
        {
            f = t.AddHours(-1);
        }

        if (t - f > TimeSpan.FromDays(1100))
        {
            f = t.AddDays(-1100);
        }

        return Results.Ok(SeriesDto.From(await metrics.QuerySeriesAsync(id, DateTime.SpecifyKind(f, DateTimeKind.Utc), DateTime.SpecifyKind(t, DateTimeKind.Utc), ct)));
    }

    private static async Task<IResult> UpdateServicesAsync(int id, ServicesUpdate input, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var device = await db.Devices.Include(d => d.WatchedServices).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Results.NotFound();
        }

        var services = (input.Services ?? Array.Empty<string>()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (services.Count > 50)
        {
            return HttpExtensions.Error("At most 50 watched services per device.");
        }

        if (services.FirstOrDefault(s => !WatchedServiceNames.IsValid(s)) is { } bad)
        {
            return HttpExtensions.Error($"\"{bad}\" is not a valid service name.");
        }

        device.WatchedServices.RemoveAll(w => !services.Contains(w.ServiceName, StringComparer.OrdinalIgnoreCase));
        foreach (var name in services.Where(s => !device.WatchedServices.Any(w => string.Equals(w.ServiceName, s, StringComparison.OrdinalIgnoreCase))))
        {
            device.WatchedServices.Add(new Core.Entities.WatchedService { ServiceName = name });
        }

        device.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "WatchedServicesUpdated", "Device", id.Inv(), string.Join(", ", services));
        return Results.NoContent();
    }

    private static async Task<IResult> ResetHostKeyAsync(int id, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var count = await db.Devices.Where(d => d.Id == id).ExecuteUpdateAsync(u => u.SetProperty(d => d.SshHostKeyFingerprint, (string?)null), ct);
        if (count == 0)
        {
            return Results.NotFound();
        }

        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "SshHostKeyCleared", "Device", id.Inv(), "The next SSH connection will record the new host key.");
        return Results.NoContent();
    }
}

/// <summary>Received and collected events: syslog, SNMP traps, Windows event-log entries (server-side filtered and paged).</summary>
public static class EventEndpoints
{
    public sealed record SyslogDto(long Id, DateTime ReceivedUtc, DateTime? MessageUtc, string SourceIp, int? DeviceId, string? DeviceName, int Facility, int Severity, string SeverityName, string? Hostname, string? AppName, string Message);

    public sealed record TrapDto(long Id, DateTime ReceivedUtc, string SourceIp, int? DeviceId, string? DeviceName, string Version, string TrapOid, string? TrapName, Severity Severity, string Summary, string VarbindsJson);

    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api").RequireAuthorization(Policies.Viewer);
        read.MapGet("/syslog", SyslogAsync);
        read.MapGet("/traps", TrapsAsync);
        read.MapGet("/windows-events", WindowsEventsAsync);
    }

    private static (int Page, int Size) Paging(int? page, int? pageSize) => (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 200, 1, 2000));

    private static async Task<Dictionary<int, string>> NamesAsync(NetWatchDbContext db, IEnumerable<int?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0 ? new Dictionary<int, string>() : await db.Devices.AsNoTracking().Where(d => list.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
    }

    /// <param name="maxSeverity">Syslog severity threshold: 3 = error and worse (0–3).</param>
    private static async Task<IResult> SyslogAsync(EventsDbContext events, NetWatchDbContext db, int? deviceId, int? maxSeverity, string? search, string? source, DateTime? from, DateTime? to, int? page, int? pageSize, CancellationToken ct)
    {
        var q = events.SyslogMessages.AsNoTracking().AsQueryable();
        if (deviceId is { } d)
        {
            q = q.Where(x => x.DeviceId == d);
        }

        if (maxSeverity is { } sev)
        {
            q = q.Where(x => x.SyslogSeverity <= sev);
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            q = q.Where(x => x.SourceIp == source.Trim());
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.Like(x.Message, term) || EF.Functions.Like(x.AppName ?? string.Empty, term) || EF.Functions.Like(x.Hostname ?? string.Empty, term));
        }

        if (from is { } f)
        {
            var fu = f.ToUniversalTime();
            q = q.Where(x => x.ReceivedUtc >= fu);
        }

        if (to is { } t)
        {
            var tu = t.ToUniversalTime();
            q = q.Where(x => x.ReceivedUtc < tu);
        }

        var (p, size) = Paging(page, pageSize);

        // Counting millions of rows is slow; report "more" instead of an exact total beyond 10,000.
        var total = await q.Take(10_001).CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var names = await NamesAsync(db, rows.Select(r => r.DeviceId), ct);
        var items = rows.Select(r => new SyslogDto(r.Id, r.ReceivedUtc, r.MessageUtc, r.SourceIp, r.DeviceId, r.DeviceId is { } id ? names.GetValueOrDefault(id) : null,
            r.Facility, r.SyslogSeverity, SeverityMapping.SyslogSeverityName(r.SyslogSeverity), r.Hostname, r.AppName, r.Message)).ToList();
        return Results.Ok(new { items, total, page = p, pageSize = size, totalCapped = total > 10_000 });
    }

    private static async Task<IResult> TrapsAsync(EventsDbContext events, NetWatchDbContext db, int? deviceId, Severity? minSeverity, string? search, DateTime? from, DateTime? to, int? page, int? pageSize, CancellationToken ct)
    {
        var q = events.SnmpTraps.AsNoTracking().AsQueryable();
        if (deviceId is { } d)
        {
            q = q.Where(x => x.DeviceId == d);
        }

        if (minSeverity is { } min)
        {
            var allowed = Enum.GetValues<Severity>().Where(s => s >= min).ToList();
            q = q.Where(x => allowed.Contains(x.Severity));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.Like(x.Summary, term) || EF.Functions.Like(x.TrapOid, term) || EF.Functions.Like(x.SourceIp, term));
        }

        if (from is { } f)
        {
            var fu = f.ToUniversalTime();
            q = q.Where(x => x.ReceivedUtc >= fu);
        }

        if (to is { } t)
        {
            var tu = t.ToUniversalTime();
            q = q.Where(x => x.ReceivedUtc < tu);
        }

        var (p, size) = Paging(page, pageSize);
        var total = await q.Take(10_001).CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var names = await NamesAsync(db, rows.Select(r => r.DeviceId), ct);
        var items = rows.Select(r => new TrapDto(r.Id, r.ReceivedUtc, r.SourceIp, r.DeviceId, r.DeviceId is { } id ? names.GetValueOrDefault(id) : null,
            r.Version, r.TrapOid, r.TrapName, r.Severity, r.Summary, r.VarbindsJson)).ToList();
        return Results.Ok(new { items, total, page = p, pageSize = size, totalCapped = total > 10_000 });
    }

    private static async Task<IResult> WindowsEventsAsync(EventsDbContext events, NetWatchDbContext db, int? deviceId, int? maxLevel, int? eventId, string? search, DateTime? from, DateTime? to, int? page, int? pageSize, CancellationToken ct)
    {
        var q = events.WindowsEvents.AsNoTracking().AsQueryable();
        if (deviceId is { } d)
        {
            q = q.Where(x => x.DeviceId == d);
        }

        if (maxLevel is { } level)
        {
            q = q.Where(x => x.Level <= level);
        }

        if (eventId is { } eid)
        {
            q = q.Where(x => x.EventId == eid);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.Like(x.Message, term) || EF.Functions.Like(x.Provider ?? string.Empty, term));
        }

        if (from is { } f)
        {
            var fu = f.ToUniversalTime();
            q = q.Where(x => x.TimestampUtc >= fu);
        }

        if (to is { } t)
        {
            var tu = t.ToUniversalTime();
            q = q.Where(x => x.TimestampUtc < tu);
        }

        var (p, size) = Paging(page, pageSize);
        var total = await q.Take(10_001).CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var names = await NamesAsync(db, rows.Select(r => (int?)r.DeviceId), ct);
        var items = rows.Select(r => new WindowsEventDto(r.Id, r.TimestampUtc, r.DeviceId, names.GetValueOrDefault(r.DeviceId), r.LogName, r.Provider, r.EventId, r.Level, r.Message)).ToList();
        return Results.Ok(new { items, total, page = p, pageSize = size, totalCapped = total > 10_000 });
    }
}
