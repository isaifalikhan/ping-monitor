using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Live;
using NetWatch.Collectors.Scheduling;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Auth;
using NetWatch.Web.Csv;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Api;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api").RequireAuthorization(Policies.Viewer);
        read.MapGet("/devices", ListAsync);
        read.MapGet("/devices/tags", TagsAsync);
        read.MapGet("/devices/{id:int}", GetAsync);
        read.MapGet("/devices/{id:int}/interfaces", InterfacesAsync);
        read.MapGet("/devices/{id:int}/sensors", SensorsAsync);
        read.MapGet("/devices/{id:int}/events", EventsAsync);
        read.MapGet("/devices/{id:int}/metrics", DeviceMetricsAsync);
        read.MapGet("/interfaces/{id:int}/metrics", InterfaceMetricsAsync);
        read.MapGet("/events", RecentEventsAsync);

        var op = app.MapGroup("/api").RequireAuthorization(Policies.Operator);
        op.MapPost("/devices/{id:int}/poll-now", PollNow);

        var admin = app.MapGroup("/api").RequireAuthorization(Policies.Admin);
        admin.MapPost("/devices", CreateAsync);
        admin.MapPut("/devices/{id:int}", UpdateAsync);
        admin.MapDelete("/devices/{id:int}", DeleteAsync);
        admin.MapPost("/devices/maintenance", MaintenanceAsync);
        admin.MapPut("/interfaces/{id:int}", UpdateInterfaceAsync);
        admin.MapPost("/interfaces/bulk", BulkUpdateInterfacesAsync);
        admin.MapPost("/devices/import", ImportAsync).DisableAntiforgery();
        admin.MapGet("/devices/import/template", ImportTemplate);
    }

    private static async Task<IResult> ListAsync(
        NetWatchDbContext db,
        LiveStateCache live,
        IActiveAlertIndex alerts,
        InventoryCache inventory,
        IDependencyStatus dependencies,
        string? search,
        DeviceType? type,
        DeviceStatus? status,
        int? groupId,
        string? tag,
        CancellationToken ct)
    {
        var query = db.Devices.AsNoTracking().Include(d => d.Tags).AsQueryable();
        if (type is { } t)
        {
            query = query.Where(d => d.Type == t);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(d => EF.Functions.Like(d.Name, term) || EF.Functions.Like(d.IpAddress, term) ||
                                     EF.Functions.Like(d.Vendor ?? string.Empty, term) || EF.Functions.Like(d.Model ?? string.Empty, term));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            query = query.Where(d => d.Tags.Any(x => x.Tag == tag));
        }

        var devices = await query.OrderBy(d => d.Name).ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().ToListAsync(ct);
        var paths = DeviceStatusHelper.GroupPaths(groups);

        if (groupId is { } gid)
        {
            var subtree = Subtree(groups, gid);
            devices = devices.Where(d => d.GroupId is { } g && subtree.Contains(g)).ToList();
        }

        var now = DateTime.UtcNow;
        var items = devices.Select(d => ToListItem(d, paths, live, alerts, inventory, dependencies, now)).ToList();
        if (status is { } s)
        {
            items = items.Where(i => i.Status == s).ToList();
        }

        return Results.Ok(items);
    }

    internal static DeviceListItem ToListItem(Device d, IReadOnlyDictionary<int, string> paths, LiveStateCache live, IActiveAlertIndex alerts, InventoryCache inventory, IDependencyStatus dependencies, DateTime now)
    {
        var l = live.GetDevice(d.Id);
        var worst = alerts.GetWorstSeverity(d.Id);
        var snapshot = inventory.GetSnapshot(d.Id);
        var maintenanceReason = snapshot?.MaintenanceReason ?? (DeviceStatusHelper.InMaintenance(d, now) ? "Maintenance mode" : null);
        var inMaintenance = maintenanceReason is not null;
        var raw = l?.Status ?? d.Status;
        var unreachableVia = raw is DeviceStatus.Down or DeviceStatus.Warning && d.ParentDeviceId is not null ? dependencies.FindUnreachableUpstream(d.Id) : null;
        return new DeviceListItem(
            d.Id, d.Name, d.IpAddress, d.Type, d.Vendor, d.Model, d.GroupId,
            d.GroupId is { } g ? paths.GetValueOrDefault(g) : null,
            DeviceStatusHelper.Effective(raw, d.IsEnabled, inMaintenance, worst), raw, worst,
            inMaintenance, d.IsEnabled, l?.LastPollUtc ?? d.LastPollUtc, d.StatusSinceUtc,
            l?.RttMs, l?.LossPct, l?.CpuPct, l?.MemPct,
            d.Tags.Select(t => t.Tag).OrderBy(t => t).ToList(),
            d.Os, maintenanceReason, unreachableVia);
    }

    internal static HashSet<int> Subtree(IReadOnlyCollection<DeviceGroup> groups, int rootId)
    {
        var result = new HashSet<int> { rootId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var g in groups)
            {
                if (g.ParentId is { } p && result.Contains(p) && result.Add(g.Id))
                {
                    added = true;
                }
            }
        }

        return result;
    }

    private static async Task<IResult> TagsAsync(NetWatchDbContext db, CancellationToken ct) =>
        Results.Ok(await db.DeviceTags.AsNoTracking().Select(t => t.Tag).Distinct().OrderBy(t => t).ToListAsync(ct));

    private static async Task<IResult> GetAsync(int id, NetWatchDbContext db, LiveStateCache live, IActiveAlertIndex alerts, InventoryCache inventory, IDependencyStatus dependencies, CancellationToken ct)
    {
        var d = await db.Devices.AsNoTracking()
            .Include(x => x.Tags)
            .Include(x => x.Credentials).ThenInclude(c => c.CredentialProfile)
            .Include(x => x.WatchedServices)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null)
        {
            return Results.NotFound();
        }

        var paths = DeviceStatusHelper.GroupPaths(await db.Groups.AsNoTracking().ToListAsync(ct));
        var parentName = d.ParentDeviceId is { } pid ? await db.Devices.Where(x => x.Id == pid).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var l = live.GetDevice(d.Id);
        var worst = alerts.GetWorstSeverity(d.Id);
        var snapshot = inventory.GetSnapshot(d.Id);
        var maintenanceReason = snapshot?.MaintenanceReason ?? (DeviceStatusHelper.InMaintenance(d, DateTime.UtcNow) ? "Maintenance mode" : null);
        var inMaintenance = maintenanceReason is not null;
        var raw = l?.Status ?? d.Status;
        var unreachableVia = raw is DeviceStatus.Down or DeviceStatus.Warning ? dependencies.FindUnreachableUpstream(d.Id) : null;

        return Results.Ok(new DeviceDetailDto(
            d.Id, d.Name, d.IpAddress, d.Hostname, d.Type, d.Os, d.Vendor, d.Model, d.SerialNumber, d.Firmware, d.SysObjectId,
            d.SysName, d.SysDescription, d.Location, d.GroupId, d.GroupId is { } g ? paths.GetValueOrDefault(g) : null,
            d.PollIntervalSec, d.IsEnabled, inMaintenance, snapshot?.MaintenanceUntilUtc ?? d.MaintenanceUntilUtc, d.ParentDeviceId, parentName,
            DeviceStatusHelper.Effective(raw, d.IsEnabled, inMaintenance, worst), raw, worst, d.StatusSinceUtc,
            l?.LastPollUtc ?? d.LastPollUtc, l?.UptimeSec ?? d.LastUptimeSec, l?.RttMs, l?.LossPct, l?.CpuPct, l?.MemPct, l?.TempC,
            d.Notes, d.Tags.Select(t => t.Tag).OrderBy(t => t).ToList(),
            d.Credentials.Select(c => new DeviceCredentialDto(c.Purpose, c.CredentialProfileId, c.CredentialProfile?.Name)).ToList(),
            d.CreatedUtc, d.UpdatedUtc,
            d.WatchedServices.Select(s => s.ServiceName).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            d.SshHostKeyFingerprint, maintenanceReason, unreachableVia, d.TlsThumbprint));
    }

    private static async Task<IResult> InterfacesAsync(int id, NetWatchDbContext db, LiveStateCache live, CancellationToken ct)
    {
        var rows = await db.Interfaces.AsNoTracking().Where(i => i.DeviceId == id).OrderBy(i => i.IfIndex).ToListAsync(ct);
        return Results.Ok(rows.Select(i =>
        {
            var l = live.GetInterface(i.Id);
            return new InterfaceDto(i.Id, i.DeviceId, i.IfIndex, i.Name, i.Description, i.Alias, i.IfType, i.SpeedBps, i.Mac,
                l?.AdminStatus ?? i.AdminStatus, l?.OperStatus ?? i.OperStatus, i.LastChangeUtc, i.IsImportant, i.IsMonitored,
                l?.InBps, l?.OutBps, l?.InUtilPct, l?.OutUtilPct, l?.ErrorsPs);
        }).ToList());
    }

    private static async Task<IResult> SensorsAsync(int id, NetWatchDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Sensors.AsNoTracking().Where(s => s.DeviceId == id).OrderBy(s => s.Kind).ThenBy(s => s.Name)
            .Select(s => new SensorDto(s.Id, s.Kind, s.Name, s.LastValue, s.Unit, s.LastState, s.LastSeenUtc)).ToListAsync(ct));

    private static async Task<IResult> EventsAsync(int id, EventsDbContext events, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 100, 1, 1000);
        var rows = await events.DeviceEvents.AsNoTracking().Where(e => e.DeviceId == id)
            .OrderByDescending(e => e.TimestampUtc).Take(take).ToListAsync(ct);
        return Results.Ok(rows.Select(e => new DeviceEventDto(e.Id, e.TimestampUtc, e.DeviceId, null, e.Kind, e.Severity, e.Message)));
    }

    private static async Task<IResult> RecentEventsAsync(EventsDbContext events, NetWatchDbContext db, int? limit, Severity? minSeverity, int? deviceId, string? search, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 5000);
        var query = events.DeviceEvents.AsNoTracking();
        if (minSeverity is { } min)
        {
            var allowed = Enum.GetValues<Severity>().Where(s => s >= min).ToList();
            query = query.Where(e => allowed.Contains(e.Severity));
        }

        if (deviceId is { } did)
        {
            query = query.Where(e => e.DeviceId == did);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(e => EF.Functions.Like(e.Message, $"%{search.Trim()}%"));
        }

        if (from is { } f)
        {
            var fu = f.ToUniversalTime();
            query = query.Where(e => e.TimestampUtc >= fu);
        }

        if (to is { } t)
        {
            var tu = t.ToUniversalTime();
            query = query.Where(e => e.TimestampUtc < tu);
        }

        var rows = await query.OrderByDescending(e => e.TimestampUtc).Take(take).ToListAsync(ct);
        var ids = rows.Where(r => r.DeviceId.HasValue).Select(r => r.DeviceId!.Value).Distinct().ToList();
        var names = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return Results.Ok(rows.Select(e => new DeviceEventDto(e.Id, e.TimestampUtc, e.DeviceId, e.DeviceId is { } d ? names.GetValueOrDefault(d) : null, e.Kind, e.Severity, e.Message)));
    }

    private static async Task<IResult> DeviceMetricsAsync(int id, DateTime? from, DateTime? to, IMetricStore metrics, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Results.Ok(SeriesDto.From(await metrics.QueryDeviceAsync(id, f, t, ct)));
    }

    private static async Task<IResult> InterfaceMetricsAsync(int id, DateTime? from, DateTime? to, IMetricStore metrics, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Results.Ok(SeriesDto.From(await metrics.QueryInterfaceAsync(id, f, t, ct)));
    }

    private static (DateTime From, DateTime To) Range(DateTime? from, DateTime? to)
    {
        var t = (to ?? DateTime.UtcNow).ToUniversalTime();
        var f = (from ?? t.AddHours(-1)).ToUniversalTime();
        if (f >= t)
        {
            f = t.AddHours(-1);
        }

        // Cap at ~3 years to keep queries bounded.
        if (t - f > TimeSpan.FromDays(1100))
        {
            f = t.AddDays(-1100);
        }

        return (DateTime.SpecifyKind(f, DateTimeKind.Utc), DateTime.SpecifyKind(t, DateTimeKind.Utc));
    }

    private static IResult PollNow(int id, PollScheduler scheduler)
    {
        scheduler.PollNow(id);
        return Results.Accepted();
    }

    private static async Task<IResult> CreateAsync(DeviceInput input, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, SettingsService settings, CancellationToken ct)
    {
        var device = new Device { CreatedUtc = DateTime.UtcNow, StatusSinceUtc = DateTime.UtcNow, PollIntervalSec = settings.Monitoring.DefaultPollIntervalSec };
        if (await ApplyAsync(device, input, db, isNew: true, ct) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "DeviceCreated", "Device", device.Id.Inv(), $"{device.Name} ({device.IpAddress})");
        return Results.Created($"/api/devices/{device.Id}", new { device.Id });
    }

    private static async Task<IResult> UpdateAsync(int id, DeviceInput input, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var device = await db.Devices.Include(d => d.Tags).Include(d => d.Credentials).Include(d => d.WatchedServices).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Results.NotFound();
        }

        if (await ApplyAsync(device, input, db, isNew: false, ct) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        await db.SaveChangesAsync(ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "DeviceUpdated", "Device", id.Inv(), JsonSerializer.Serialize(input with { Notes = null }));
        return Results.NoContent();
    }

    /// <summary>Validates input and copies it onto the entity. Returns an error message or null.</summary>
    private static async Task<string?> ApplyAsync(Device device, DeviceInput input, NetWatchDbContext db, bool isNew, CancellationToken ct)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            return "Name is required (max 200 characters).";
        }

        if (!IPAddress.TryParse(input.IpAddress?.Trim(), out var ip))
        {
            return "A valid IP address is required.";
        }

        var ipText = ip.ToString();
        if (await db.Devices.AnyAsync(d => d.IpAddress == ipText && d.Id != device.Id, ct))
        {
            return $"Another device already uses {ipText}.";
        }

        if (input.GroupId is { } gid && !await db.Groups.AnyAsync(g => g.Id == gid, ct))
        {
            return "Selected group does not exist.";
        }

        if (input.ParentDeviceId is { } pid && (pid == device.Id || !await db.Devices.AnyAsync(d => d.Id == pid, ct)))
        {
            return "Parent device is invalid.";
        }

        if (input.SnmpCredentialId is { } cid &&
            !await db.CredentialProfiles.AnyAsync(c => c.Id == cid && (c.Kind == CredentialKind.SnmpV2c || c.Kind == CredentialKind.SnmpV3), ct))
        {
            return "Selected SNMP credential does not exist.";
        }

        if (input.ManagementCredentialId is { } mid &&
            !await db.CredentialProfiles.AnyAsync(c => c.Id == mid && (c.Kind == CredentialKind.Windows || c.Kind == CredentialKind.Ssh), ct))
        {
            return "Selected Windows/SSH credential does not exist.";
        }

        if (input.VcenterCredentialId is { } vid &&
            !await db.CredentialProfiles.AnyAsync(c => c.Id == vid && c.Kind == CredentialKind.VCenter, ct))
        {
            return "Selected vCenter credential does not exist.";
        }

        IReadOnlyList<string> requested = input.WatchedServices ?? Array.Empty<string>();
        if (device.Id == 0 && input.Type == DeviceType.DomainController && requested.Count == 0)
        {
            // New domain controllers watch the AD core services unless the operator chose others.
            requested = DcDefaults.WatchedServices;
        }

        var services = requested
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (services.Count > 50)
        {
            return "At most 50 watched services per device.";
        }

        if (services.FirstOrDefault(s => !WatchedServiceNames.IsValid(s)) is { } badService)
        {
            return $"\"{badService}\" is not a valid service name (letters, digits, space and . _ - @ $ : only).";
        }

        if (device.IpAddress != ipText)
        {
            // A different address may be a different machine: re-learn its SSH host key.
            device.SshHostKeyFingerprint = null;
            device.TlsThumbprint = null;
        }

        device.Name = name;
        device.IpAddress = ipText;
        device.Hostname = string.IsNullOrWhiteSpace(input.Hostname) ? null : input.Hostname.Trim();
        device.Type = input.Type;
        device.Os = input.Os;
        device.Location = string.IsNullOrWhiteSpace(input.Location) ? null : input.Location.Trim();
        device.GroupId = input.GroupId;
        device.PollIntervalSec = Math.Clamp(input.PollIntervalSec ?? device.PollIntervalSec, 15, 3600);
        device.IsEnabled = input.IsEnabled;
        device.ParentDeviceId = input.ParentDeviceId;
        device.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        device.UpdatedUtc = DateTime.UtcNow;

        var tags = (input.Tags ?? Array.Empty<string>())
            .Select(t => t.Trim())
            .Where(t => t.Length is > 0 and <= 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        device.Tags.RemoveAll(t => !tags.Contains(t.Tag, StringComparer.OrdinalIgnoreCase));
        foreach (var tag in tags.Where(t => !device.Tags.Any(x => string.Equals(x.Tag, t, StringComparison.OrdinalIgnoreCase))))
        {
            device.Tags.Add(new DeviceTag { Tag = tag });
        }

        device.Credentials.RemoveAll(c => c.Purpose == CredentialPurpose.Snmp && c.CredentialProfileId != input.SnmpCredentialId);
        if (input.SnmpCredentialId is { } snmpId && !device.Credentials.Any(c => c.Purpose == CredentialPurpose.Snmp))
        {
            device.Credentials.Add(new DeviceCredential { Purpose = CredentialPurpose.Snmp, CredentialProfileId = snmpId });
        }

        device.Credentials.RemoveAll(c => c.Purpose == CredentialPurpose.Management && c.CredentialProfileId != input.ManagementCredentialId);
        if (input.ManagementCredentialId is { } managementId && !device.Credentials.Any(c => c.Purpose == CredentialPurpose.Management))
        {
            device.Credentials.Add(new DeviceCredential { Purpose = CredentialPurpose.Management, CredentialProfileId = managementId });
        }

        device.Credentials.RemoveAll(c => c.Purpose == CredentialPurpose.VCenter && c.CredentialProfileId != input.VcenterCredentialId);
        if (input.VcenterCredentialId is { } vcenterId && !device.Credentials.Any(c => c.Purpose == CredentialPurpose.VCenter))
        {
            device.Credentials.Add(new DeviceCredential { Purpose = CredentialPurpose.VCenter, CredentialProfileId = vcenterId });
        }

        if (input.WatchedServices is not null || (device.Id == 0 && services.Count > 0))
        {
            device.WatchedServices.RemoveAll(w => !services.Contains(w.ServiceName, StringComparer.OrdinalIgnoreCase));
            foreach (var name2 in services.Where(s => !device.WatchedServices.Any(w => string.Equals(w.ServiceName, s, StringComparison.OrdinalIgnoreCase))))
            {
                device.WatchedServices.Add(new WatchedService { ServiceName = name2 });
            }
        }

        return null;
    }

    private static async Task<IResult> DeleteAsync(int id, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Results.NotFound();
        }

        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "DeviceDeleted", "Device", id.Inv(), $"{device.Name} ({device.IpAddress})");
        return Results.NoContent();
    }

    private static async Task<IResult> MaintenanceAsync(MaintenanceRequest request, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        if (request.DeviceIds is not { Count: > 0 })
        {
            return HttpExtensions.Error("Select at least one device.");
        }

        var until = request.Enabled ? request.UntilUtc?.ToUniversalTime() : null;
        var count = await db.Devices.Where(d => request.DeviceIds.Contains(d.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.MaintenanceMode, request.Enabled)
            .SetProperty(d => d.MaintenanceUntilUtc, until)
            .SetProperty(d => d.UpdatedUtc, DateTime.UtcNow), ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), request.Enabled ? "MaintenanceStarted" : "MaintenanceEnded", "Device",
            string.Join(",", request.DeviceIds), until is null ? null : $"until {until:O}");
        return Results.Ok(new { updated = count });
    }

    private static async Task<IResult> UpdateInterfaceAsync(int id, InterfaceUpdate update, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var row = await db.Interfaces.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        row.IsImportant = update.IsImportant ?? row.IsImportant;
        row.IsMonitored = update.IsMonitored ?? row.IsMonitored;
        await db.SaveChangesAsync(ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "InterfaceUpdated", "Interface", id.Inv(), $"{row.Name}: important={row.IsImportant}, monitored={row.IsMonitored}");
        return Results.NoContent();
    }

    private static async Task<IResult> BulkUpdateInterfacesAsync(BulkInterfaceUpdate update, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        if (update.InterfaceIds is not { Count: > 0 })
        {
            return HttpExtensions.Error("Select at least one interface.");
        }

        var rows = await db.Interfaces.Where(i => update.InterfaceIds.Contains(i.Id)).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.IsImportant = update.IsImportant ?? row.IsImportant;
            row.IsMonitored = update.IsMonitored ?? row.IsMonitored;
        }

        await db.SaveChangesAsync(ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "InterfacesUpdated", "Interface", string.Join(",", update.InterfaceIds.Take(50)),
            $"important={update.IsImportant?.ToString() ?? "-"}, monitored={update.IsMonitored?.ToString() ?? "-"}");
        return Results.Ok(new { updated = rows.Count });
    }

    private static async Task<IResult> ImportAsync(HttpRequest request, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, SettingsService settings, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return HttpExtensions.Error("Upload a CSV file.");
        }

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return HttpExtensions.Error("Upload a CSV file.");
        }

        if (file.Length > 5 * 1024 * 1024)
        {
            return HttpExtensions.Error("CSV file is larger than 5 MB.");
        }

        await using var stream = file.OpenReadStream();
        var parsed = DeviceCsvParser.Parse(stream);
        var result = await DeviceCsvImporter.ImportAsync(parsed, db, settings.Monitoring.DefaultPollIntervalSec, ct);
        if (result.Created > 0)
        {
            notifier.NotifyInventoryChanged();
        }

        await audit.WriteAsync(http.Actor(), "DevicesImported", "Device", null, $"{file.FileName}: {result.Created} created, {result.Skipped} skipped, {result.Errors.Count} errors");
        return Results.Ok(result);
    }

    private static IResult ImportTemplate() =>
        Results.File(System.Text.Encoding.UTF8.GetBytes(DeviceCsvParser.Template), "text/csv", "netwatch-devices-template.csv");
}
