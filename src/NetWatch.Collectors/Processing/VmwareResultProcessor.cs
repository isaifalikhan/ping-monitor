using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Vmware;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Metrics;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Config;
using NetWatch.Data.Events;

namespace NetWatch.Collectors.Processing;

/// <summary>
/// VMware part of poll processing: builds the vSphere request for vCenter/ESXi devices and turns the result into
/// stored inventory (hosts, VMs, datastores, snapshots, triggered alarms), vCenter events, metric series and alert
/// signals. VMs and hosts that are also NetWatch devices (same IP or name) get their alerts on that device.
/// </summary>
public sealed class VmwareResultProcessor
{
    /// <summary>Event-bookmark "log name" used for vCenter events.</summary>
    public const string BookmarkLog = "vcenter";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMetricStore _metrics;
    private readonly IAlertSignalSink _alerts;
    private readonly SettingsService _settings;
    private readonly CredentialResolver _credentials;
    private readonly InventoryCache _inventory;
    private readonly ILogger<VmwareResultProcessor> _logger;

    public VmwareResultProcessor(IServiceScopeFactory scopeFactory, IMetricStore metrics, IAlertSignalSink alerts, SettingsService settings, CredentialResolver credentials, InventoryCache inventory, ILogger<VmwareResultProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _alerts = alerts;
        _settings = settings;
        _credentials = credentials;
        _inventory = inventory;
        _logger = logger;
    }

    public async Task<VmwarePollRequest?> PrepareAsync(DeviceSnapshot device, CancellationToken ct)
    {
        if (device.VcenterCredentialId is not { } credentialId || await _credentials.GetVcenterAsync(credentialId, ct) is not { } credential)
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        var bookmark = await events.EventBookmarks.AsNoTracking()
            .Where(b => b.DeviceId == device.Id && b.LogName == BookmarkLog)
            .Select(b => new { b.LastRecordId, b.UpdatedUtc })
            .FirstOrDefaultAsync(ct);
        var s = _settings.Vmware;
        return new VmwarePollRequest(
            device.IpAddress,
            credential,
            device.TlsThumbprint,
            s.TrustOnFirstUse,
            s.ImportEvents,
            s.ImportAlarms,
            s.CollectCpuReady,
            s.MaxEventsPerPoll,
            bookmark is null ? null : (bookmark.LastRecordId, bookmark.UpdatedUtc),
            TimeSpan.FromSeconds(s.TimeoutSec));
    }

    /// <summary>
    /// Signals for VMware objects go to the matching NetWatch device (Instance null), else to the vCenter device
    /// with the object name as instance.
    /// </summary>
    private (int DeviceId, string? Instance) Target(int sourceId, string? ip, string name)
    {
        var device = _inventory.FindByAddressOrName(ip, name);
        return device is not null && device.Id != sourceId ? (device.Id, null) : (sourceId, name);
    }

    public async Task ProcessAsync(DeviceSnapshot source, DevicePollResult result, NetWatchDbContext db, EventsDbContext events, List<DeviceEvent> deviceEvents, CancellationToken ct)
    {
        if (result.Vmware is not { } data)
        {
            return;
        }

        var now = result.TimestampUtc;
        var tsMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        _alerts.Publish(new StateSignal(source.Id, null, StateKeys.VcenterUnreachable, null, !data.Ok,
            data.Ok ? "vSphere API is responding" : $"vSphere API collection failed: {data.Error}", now));

        if (data.NewThumbprint is { } thumbprint)
        {
            await db.Devices.Where(d => d.Id == source.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.TlsThumbprint, thumbprint), ct);
            deviceEvents.Add(new DeviceEvent { DeviceId = source.Id, TimestampUtc = now, Kind = DeviceEventKind.Server, Severity = Severity.Info, Message = $"vCenter certificate pinned (SHA-256 {thumbprint})." });
            _inventory.NotifyInventoryChanged();
        }

        if (!data.Ok)
        {
            return;
        }

        var hostNames = data.Hosts.ToDictionary(h => h.MoRef, h => h.Name, StringComparer.Ordinal);
        var dsNames = data.Datastores.ToDictionary(d => d.MoRef, d => d.Name, StringComparer.Ordinal);
        var vmNames = data.Vms.ToDictionary(v => v.MoRef, v => v.Name, StringComparer.Ordinal);

        // ---- hosts
        var hosts = await db.VmwareHosts.Where(h => h.SourceDeviceId == source.Id).ToDictionaryAsync(h => h.MoRef, StringComparer.Ordinal, ct);
        foreach (var h in data.Hosts)
        {
            if (!hosts.TryGetValue(h.MoRef, out var row))
            {
                row = new VmwareHost { SourceDeviceId = source.Id, MoRef = h.MoRef };
                db.VmwareHosts.Add(row);
                hosts[h.MoRef] = row;
            }

            var (deviceId, instance) = Target(source.Id, null, h.Name);
            row.Name = Trim(h.Name, 255)!;
            row.Cluster = Trim(h.Cluster, 255);
            row.ConnectionState = h.ConnectionState;
            row.PowerState = h.PowerState;
            row.InMaintenance = h.InMaintenance;
            row.OverallStatus = h.OverallStatus;
            row.Vendor = Trim(h.Vendor, 100);
            row.Model = Trim(h.Model, 100);
            row.Product = Trim(h.Product, 200);
            row.CpuCores = h.CpuCores;
            row.CpuTotalMhz = h.CpuTotalMhz;
            row.CpuUsageMhz = h.CpuUsageMhz;
            row.MemTotalBytes = h.MemTotalBytes;
            row.MemUsageBytes = h.MemUsageBytes;
            row.BootTimeUtc = h.BootTimeUtc;
            row.SensorWarnings = h.SensorWarnings;
            row.SensorAlarms = h.SensorAlarms;
            row.DeviceId = deviceId == source.Id ? null : deviceId;
            row.LastSeenUtc = now;

            var connected = h.ConnectionState == "connected";
            _alerts.Publish(new StateSignal(deviceId, null, StateKeys.HostNotConnected, instance, !connected && !h.InMaintenance,
                connected ? $"ESXi host {h.Name} is connected" : $"ESXi host {h.Name} is {h.ConnectionState}", now, instance));
            _alerts.Publish(new StateSignal(deviceId, null, StateKeys.HostMaintenance, instance, h.InMaintenance,
                h.InMaintenance ? $"ESXi host {h.Name} is in maintenance mode" : $"ESXi host {h.Name} left maintenance mode", now, instance));
            _alerts.Publish(new StateSignal(deviceId, null, StateKeys.HostHardware, instance, h.SensorAlarms > 0,
                h.SensorAlarms > 0 ? $"ESXi host {h.Name} hardware sensors in alarm: {string.Join(", ", h.SensorProblems.Take(5))}" : $"ESXi host {h.Name} hardware sensors OK", now, instance));
            if (connected)
            {
                Metric(deviceId, MetricKeys.HostCpu, instance, h.CpuPct, now);
                Metric(deviceId, MetricKeys.HostMemory, instance, h.MemPct, now);
                Series(source.Id, MetricKeys.HostCpu, h.Name, h.CpuPct, tsMs);
                Series(source.Id, MetricKeys.HostMemory, h.Name, h.MemPct, tsMs);
            }
        }

        db.VmwareHosts.RemoveRange(hosts.Values.Where(r => r.LastSeenUtc < now));

        // ---- datastores
        var datastores = await db.VmwareDatastores.Where(d => d.SourceDeviceId == source.Id).ToDictionaryAsync(d => d.MoRef, StringComparer.Ordinal, ct);
        foreach (var d in data.Datastores)
        {
            if (!datastores.TryGetValue(d.MoRef, out var row))
            {
                row = new VmwareDatastore { SourceDeviceId = source.Id, MoRef = d.MoRef };
                db.VmwareDatastores.Add(row);
                datastores[d.MoRef] = row;
            }

            row.Name = Trim(d.Name, 255)!;
            row.Type = d.Type;
            row.CapacityBytes = d.CapacityBytes;
            row.FreeBytes = d.FreeBytes;
            row.UncommittedBytes = d.UncommittedBytes;
            row.Accessible = d.Accessible;
            row.MaintenanceMode = d.MaintenanceMode;
            row.HostCount = d.HostCount;
            row.VmCount = d.VmCount;
            row.LastSeenUtc = now;

            _alerts.Publish(new StateSignal(source.Id, null, StateKeys.DatastoreInaccessible, d.Name, !d.Accessible,
                d.Accessible ? $"Datastore {d.Name} is accessible" : $"Datastore {d.Name} is not accessible", now, d.Name));
            if (d.CapacityBytes > 0)
            {
                Metric(source.Id, MetricKeys.DatastoreUsed, d.Name, d.UsedPct, now);
                Series(source.Id, MetricKeys.DatastoreUsed, d.Name, d.UsedPct, tsMs);
            }
        }

        db.VmwareDatastores.RemoveRange(datastores.Values.Where(r => r.LastSeenUtc < now));

        // ---- VMs
        var snapshotCounts = data.Snapshots.GroupBy(s => s.VmMoRef).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var vms = await db.VmwareVms.Where(v => v.SourceDeviceId == source.Id).ToDictionaryAsync(v => v.MoRef, StringComparer.Ordinal, ct);
        foreach (var v in data.Vms)
        {
            if (!vms.TryGetValue(v.MoRef, out var row))
            {
                row = new VmwareVm { SourceDeviceId = source.Id, MoRef = v.MoRef };
                db.VmwareVms.Add(row);
                vms[v.MoRef] = row;
            }

            var (deviceId, instance) = Target(source.Id, v.IpAddress, v.Name);
            data.CpuReadyPct.TryGetValue(v.MoRef, out var ready);
            var hasReady = data.CpuReadyPct.ContainsKey(v.MoRef);
            row.Name = Trim(v.Name, 255)!;
            row.HostMoRef = v.HostMoRef;
            row.HostName = v.HostMoRef is { } hm ? hostNames.GetValueOrDefault(hm) : null;
            row.PowerState = v.PowerState;
            row.GuestOs = Trim(v.GuestOs, 200);
            row.IpAddress = Trim(v.IpAddress, 64);
            row.GuestHostName = Trim(v.GuestHostName, 255);
            row.ToolsRunningStatus = v.ToolsRunningStatus;
            row.ToolsVersionStatus = v.ToolsVersionStatus;
            row.OverallStatus = v.OverallStatus;
            row.NumCpu = v.NumCpu;
            row.MemoryMb = v.MemoryMb;
            row.CpuPct = v.PoweredOn ? v.CpuPct : null;
            row.CpuReadyPct = v.PoweredOn && hasReady ? ready : null;
            row.MemPct = v.PoweredOn ? v.MemPct : null;
            row.BalloonedMb = v.BalloonedMb;
            row.CommittedBytes = v.CommittedBytes;
            row.UncommittedBytes = v.UncommittedBytes;
            row.GuestDiskMaxPct = v.GuestDiskMaxPct;
            row.Datastores = Trim(string.Join(", ", v.DatastoreMoRefs.Select(d => dsNames.GetValueOrDefault(d, d))), 1000);
            row.SnapshotCount = snapshotCounts.GetValueOrDefault(v.MoRef);
            row.BootTimeUtc = v.BootTimeUtc;
            row.DeviceId = deviceId == source.Id ? null : deviceId;
            row.LastSeenUtc = now;

            var toolsProblem = v.PoweredOn && v.ToolsRunningStatus is not null and not "guestToolsRunning";
            _alerts.Publish(new StateSignal(deviceId, null, StateKeys.VmToolsNotRunning, instance, toolsProblem,
                toolsProblem ? $"VMware Tools are not running on {v.Name} ({v.ToolsVersionStatus ?? v.ToolsRunningStatus})" : $"VMware Tools running on {v.Name}", now, instance));
            if (v.PoweredOn)
            {
                if (hasReady)
                {
                    Metric(deviceId, MetricKeys.VmCpuReady, instance, ready, now);
                    Series(source.Id, MetricKeys.VmCpuReady, v.Name, ready, tsMs);
                }

                Metric(deviceId, MetricKeys.VmBalloonMb, instance, v.BalloonedMb, now);
            }
        }

        db.VmwareVms.RemoveRange(vms.Values.Where(r => r.LastSeenUtc < now));

        // ---- snapshots (replaced every poll; deleted snapshots clear their alerts with a zero value)
        var oldSnapshots = await db.VmwareSnapshots.Where(s => s.SourceDeviceId == source.Id).ToListAsync(ct);
        var current = data.Snapshots.Select(s => s.MoRef).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in oldSnapshots.Where(s => !current.Contains(s.MoRef)))
        {
            var vm = data.Vms.FirstOrDefault(v => v.MoRef == gone.VmMoRef);
            var (deviceId, owner) = Target(source.Id, vm?.IpAddress, gone.VmName);
            var inst = owner is null ? gone.Name : SnapshotInstance(gone.VmName, gone.Name);
            _alerts.Publish(new MetricSignal(deviceId, null, MetricKeys.SnapshotAgeDays, inst, 0, now, inst));
            _alerts.Publish(new MetricSignal(deviceId, null, MetricKeys.SnapshotSizeGb, inst, 0, now, inst));
        }

        db.VmwareSnapshots.RemoveRange(oldSnapshots);
        foreach (var s in data.Snapshots)
        {
            var vmName = vmNames.GetValueOrDefault(s.VmMoRef, s.VmMoRef);
            db.VmwareSnapshots.Add(new VmwareSnapshot
            {
                SourceDeviceId = source.Id,
                VmMoRef = s.VmMoRef,
                VmName = Trim(vmName, 255)!,
                MoRef = s.MoRef,
                Name = Trim(s.Name, 255)!,
                Description = Trim(s.Description, 1000),
                CreatedUtc = s.CreatedUtc,
                SizeBytes = s.SizeBytes,
                IsCurrent = s.IsCurrent,
                LastSeenUtc = now,
            });

            var vm = data.Vms.FirstOrDefault(v => v.MoRef == s.VmMoRef);
            var (deviceId, owner) = Target(source.Id, vm?.IpAddress, vmName);
            var inst = owner is null ? s.Name : SnapshotInstance(vmName, s.Name);
            _alerts.Publish(new MetricSignal(deviceId, null, MetricKeys.SnapshotAgeDays, inst, Math.Round((now - s.CreatedUtc).TotalDays, 1), now, inst));
            if (s.SizeBytes is { } size)
            {
                _alerts.Publish(new MetricSignal(deviceId, null, MetricKeys.SnapshotSizeGb, inst, Math.Round(size / 1073741824.0, 1), now, inst));
            }
        }

        // ---- triggered vCenter alarms
        if (data.Alarms is { } alarms)
        {
            var oldAlarms = await db.VmwareAlarms.Where(a => a.SourceDeviceId == source.Id).ToListAsync(ct);
            var activeKeys = alarms.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in oldAlarms.Where(a => !activeKeys.Contains(a.Key) || alarms.First(x => x.Key == a.Key).Status != a.Status))
            {
                var (deviceId, owner) = AlarmTarget(source.Id, gone.EntityMoRef, gone.EntityName, data);
                var key = gone.Status == "red" ? StateKeys.VcenterAlarmRed : StateKeys.VcenterAlarmYellow;
                var inst = owner is null ? gone.AlarmName : AlarmInstance(gone.AlarmName, gone.EntityName ?? gone.EntityMoRef);
                _alerts.Publish(new StateSignal(deviceId, null, key, inst, false, $"vCenter alarm \"{gone.AlarmName}\" cleared on {gone.EntityName}", now, inst));
            }

            db.VmwareAlarms.RemoveRange(oldAlarms);
            foreach (var a in alarms)
            {
                db.VmwareAlarms.Add(new VmwareAlarm
                {
                    SourceDeviceId = source.Id,
                    Key = Trim(a.Key, 128)!,
                    AlarmName = Trim(a.AlarmName, 255)!,
                    EntityMoRef = a.EntityMoRef,
                    EntityName = Trim(a.EntityName, 255),
                    Status = a.Status,
                    Acknowledged = a.Acknowledged,
                    TriggeredUtc = a.TriggeredUtc,
                    LastSeenUtc = now,
                });

                var (deviceId, owner) = AlarmTarget(source.Id, a.EntityMoRef, a.EntityName, data);
                var key = a.Status == "red" ? StateKeys.VcenterAlarmRed : StateKeys.VcenterAlarmYellow;
                var inst = owner is null ? a.AlarmName : AlarmInstance(a.AlarmName, a.EntityName ?? a.EntityMoRef);
                _alerts.Publish(new StateSignal(deviceId, null, key, inst, !a.Acknowledged,
                    a.Acknowledged ? $"vCenter alarm \"{a.AlarmName}\" on {a.EntityName} acknowledged in vCenter" : $"vCenter alarm \"{a.AlarmName}\" ({a.Status}) on {a.EntityName}", now, inst));
            }
        }

        await db.SaveChangesAsync(ct);

        // ---- events
        if (data.Events.Count > 0 || data.NewEventBookmark is not null)
        {
            await EventsAsync(source, data, events, now, ct);
        }
    }

    private (int DeviceId, string? Instance) AlarmTarget(int sourceId, string entityMoRef, string? entityName, VmwarePollData data)
    {
        var vm = data.Vms.FirstOrDefault(v => v.MoRef == entityMoRef);
        return vm is not null ? Target(sourceId, vm.IpAddress, vm.Name) : Target(sourceId, null, entityName ?? entityMoRef);
    }

    private static string SnapshotInstance(string vm, string snapshot) => Trim($"{vm} / {snapshot}", 200)!;

    private static string AlarmInstance(string alarm, string entity) => Trim($"{alarm} @ {entity}", 200)!;

    private async Task EventsAsync(DeviceSnapshot source, VmwarePollData data, EventsDbContext events, DateTime now, CancellationToken ct)
    {
        var known = data.Events.Count == 0
            ? new HashSet<long>()
            : (await events.VmwareEvents.AsNoTracking()
                .Where(e => e.SourceDeviceId == source.Id && e.EventKey >= data.Events.Min(x => x.Key))
                .Select(e => e.EventKey).ToListAsync(ct)).ToHashSet();

        foreach (var e in data.Events.Where(e => !known.Contains(e.Key)).OrderBy(e => e.Key))
        {
            events.VmwareEvents.Add(new VmwareEvent
            {
                SourceDeviceId = source.Id,
                EventKey = e.Key,
                TimestampUtc = e.CreatedUtc,
                EventType = Trim(e.EventType, 128)!,
                Severity = e.Severity,
                UserName = Trim(e.UserName, 255),
                EntityName = Trim(e.EntityName, 255),
                Message = e.Message,
            });

            var vm = e.EntityName is null ? null : data.Vms.FirstOrDefault(v => v.Name == e.EntityName);
            var (deviceId, _) = Target(source.Id, vm?.IpAddress, e.EntityName ?? source.Name);
            _alerts.Publish(new EventSignal(deviceId, EventKeys.VmwareEvent, $"{e.EventType}: {e.Message}", e.CreatedUtc, e.Severity, e.EventType));
        }

        if (data.NewEventBookmark is { } bookmark)
        {
            var row = await events.EventBookmarks.FirstOrDefaultAsync(b => b.DeviceId == source.Id && b.LogName == BookmarkLog, ct);
            if (row is null)
            {
                events.EventBookmarks.Add(new EventBookmark { DeviceId = source.Id, LogName = BookmarkLog, LastRecordId = bookmark.Key, UpdatedUtc = bookmark.TimeUtc });
            }
            else
            {
                row.LastRecordId = bookmark.Key;
                row.UpdatedUtc = bookmark.TimeUtc;
            }
        }

        try
        {
            await events.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Could not store vCenter events of {Device}", source.Name);
            events.ChangeTracker.Clear();
        }
    }

    private void Metric(int deviceId, string key, string? instance, double? value, DateTime now)
    {
        if (value is { } v && double.IsFinite(v))
        {
            _alerts.Publish(new MetricSignal(deviceId, null, key, instance, v, now, instance));
        }
    }

    private void Series(int deviceId, string key, string instance, double? value, long tsMs)
    {
        if (value is { } v && double.IsFinite(v))
        {
            _metrics.Enqueue(new GenericSample(_metrics.GetOrCreateSeries(deviceId, key, instance), tsMs, v));
        }
    }

    private static string? Trim(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s.Trim() : s[..max];
}
