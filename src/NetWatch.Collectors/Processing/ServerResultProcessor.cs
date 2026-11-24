using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Servers;
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
/// Server part of poll processing: builds the WinRM/WMI/SSH request (credential, watched services, event-log
/// bookmarks) and turns the collected data into server facts, volumes, service states, Windows events, generic
/// metric series and alert signals.
/// </summary>
public sealed class ServerResultProcessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMetricStore _metrics;
    private readonly IAlertSignalSink _alerts;
    private readonly SettingsService _settings;
    private readonly CredentialResolver _credentials;
    private readonly IInventoryChangeNotifier _inventory;
    private readonly ILogger<ServerResultProcessor> _logger;
    private readonly ConcurrentDictionary<int, ServerRuntime> _runtime = new();

    public ServerResultProcessor(
        IServiceScopeFactory scopeFactory,
        IMetricStore metrics,
        IAlertSignalSink alerts,
        SettingsService settings,
        CredentialResolver credentials,
        IInventoryChangeNotifier inventory,
        ILogger<ServerResultProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _alerts = alerts;
        _settings = settings;
        _credentials = credentials;
        _inventory = inventory;
        _logger = logger;
    }

    private sealed class ServerRuntime
    {
        public bool Loaded;
        public Dictionary<string, long> Bookmarks = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (int Id, ServiceState State)> Services = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Volumes = new(StringComparer.OrdinalIgnoreCase);
        public (ulong Rx, ulong Tx, DateTime Ts)? NetCounters;
        public bool? PendingReboot;
        public DateTime LastVolumePersistUtc;
    }

    public void Forget(int deviceId) => _runtime.TryRemove(deviceId, out _);

    /// <summary>True when disk volumes should be read over SNMP (server without a WinRM/SSH credential).</summary>
    public static bool WantsSnmpStorage(DeviceSnapshot device) => device.IsServerType && device.ManagementCredentialId is null;

    public async Task<ServerPollRequest?> PrepareAsync(DeviceSnapshot device, CancellationToken ct)
    {
        // On switches and routers the SSH credential is for their CLI (static MAC changes), not for Linux statistics.
        if (device.ManagementCredentialId is not { } credentialId || device.Type is DeviceType.Switch or DeviceType.Router)
        {
            return null;
        }

        var credential = await _credentials.GetManagementAsync(credentialId, ct);
        if (credential is null)
        {
            return null;
        }

        var rt = await GetRuntimeAsync(device.Id, ct);
        var s = _settings.Servers;
        return new ServerPollRequest(
            device.IpAddress,
            credential,
            device.WatchedServices ?? Array.Empty<string>(),
            new Dictionary<string, long>(rt.Bookmarks, StringComparer.OrdinalIgnoreCase),
            device.SshHostKeyFingerprint,
            s.ReadEventLogs && credential.Kind == CredentialKind.Windows,
            s.IncludeWarnings,
            s.MaxEventsPerPoll,
            s.WinRmHttps && s.WinRmPort == 5985 ? 5986 : s.WinRmPort,
            s.WinRmHttps,
            s.DcomFallback,
            s.SshPort,
            TimeSpan.FromSeconds(s.TimeoutSec),
            _settings.Hardware.ReadGpus && device.Type is DeviceType.Server or DeviceType.VM,
            _settings.Hardware.RunNvidiaSmi);
    }

    /// <summary>Processes the server part of a poll result. Must run before device-level metrics are published.</summary>
    /// <param name="pingReachable">Ping or SNMP answered (used to decide whether a collection failure is worth an alert).</param>
    public async Task ProcessAsync(DeviceSnapshot device, DevicePollResult result, bool pingReachable, NetWatchDbContext db, EventsDbContext events, List<DeviceEvent> deviceEvents, CancellationToken ct)
    {
        if (result.Server is not { } server)
        {
            return;
        }

        var now = result.TimestampUtc;
        var tsMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        var rt = await GetRuntimeAsync(device.Id, ct);
        var managed = server.Method != ServerCollectionMethod.Snmp;

        // Device-level CPU/memory/uptime come from the server collector when SNMP does not provide them.
        if (server.Ok)
        {
            result.CpuPct ??= server.CpuPct;
            result.MemPct ??= server.MemPct;
            if (result.UptimeSec is null && server.LastBootUtc is { } boot && boot <= now)
            {
                result.UptimeSec = (long)(now - boot).TotalSeconds;
            }
        }

        if (managed)
        {
            await UpsertInfoAsync(db, device.Id, server, now, ct);
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.ServerCollectionFailed, null, !server.Ok && pingReachable,
                server.Ok ? $"{MethodName(server.Method)} collection is working" : $"{MethodName(server.Method)} collection failed: {server.Error}", now));
        }

        if (!server.Ok)
        {
            return;
        }

        await VolumesAsync(db, device, rt, server, now, tsMs, ct);
        await ServicesAsync(db, device, rt, server, deviceEvents, now, ct);

        if (server.CpuCount is > 0 && server.Load5 is { } load5)
        {
            var perCpu = Math.Round(load5 / server.CpuCount.Value, 2);
            _alerts.Publish(new MetricSignal(device.Id, null, MetricKeys.LoadPerCpu, null, perCpu, now));
            Series(device.Id, MetricKeys.LoadPerCpu, null, perCpu, tsMs);
        }

        var (inBps, outBps) = NetworkRates(rt, server, now);
        if (inBps is { } nin)
        {
            Series(device.Id, MetricKeys.NetInBps, null, nin, tsMs);
        }

        if (outBps is { } nout)
        {
            Series(device.Id, MetricKeys.NetOutBps, null, nout, tsMs);
        }

        if (server.PendingReboot is { } pending)
        {
            if (rt.PendingReboot != pending && rt.PendingReboot is not null)
            {
                deviceEvents.Add(new DeviceEvent
                {
                    DeviceId = device.Id,
                    TimestampUtc = now,
                    Kind = DeviceEventKind.Server,
                    Severity = Severity.Info,
                    Message = pending ? "A reboot is pending (updates installed)." : "Pending reboot cleared.",
                });
            }

            rt.PendingReboot = pending;
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.PendingReboot, null, pending,
                pending ? $"{device.Name} needs a reboot to finish installing updates" : $"{device.Name} has no pending reboot", now));
        }

        if (server.NewSshHostKeyFingerprint is { } fingerprint)
        {
            await db.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.SshHostKeyFingerprint, fingerprint), ct);
            deviceEvents.Add(new DeviceEvent
            {
                DeviceId = device.Id,
                TimestampUtc = now,
                Kind = DeviceEventKind.Server,
                Severity = Severity.Info,
                Message = $"SSH host key recorded (SHA256:{fingerprint}).",
            });
            _inventory.NotifyInventoryChanged();
        }

        if (server.NewBookmarks is { } bookmarks)
        {
            await WindowsEventsAsync(events, device, rt, server, bookmarks, now, ct);
        }
    }

    private static string MethodName(ServerCollectionMethod m) => m switch
    {
        ServerCollectionMethod.WinRm => "WinRM",
        ServerCollectionMethod.Dcom => "WMI (DCOM)",
        ServerCollectionMethod.Ssh => "SSH",
        ServerCollectionMethod.Snmp => "SNMP",
        _ => "Server",
    };

    private void Series(int deviceId, string key, string? instance, double value, long tsMs)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        var id = _metrics.GetOrCreateSeries(deviceId, key, instance);
        _metrics.Enqueue(new GenericSample(id, tsMs, value));
    }

    private static (double? In, double? Out) NetworkRates(ServerRuntime rt, ServerPollData server, DateTime now)
    {
        if (server.NetInBps is not null || server.NetOutBps is not null)
        {
            return (server.NetInBps, server.NetOutBps);
        }

        if (server.NetRxBytes is not { } rx || server.NetTxBytes is not { } tx)
        {
            return (null, null);
        }

        (double?, double?) rates = (null, null);
        if (rt.NetCounters is { } prev && now > prev.Ts && rx >= prev.Rx && tx >= prev.Tx)
        {
            var seconds = (now - prev.Ts).TotalSeconds;
            rates = ((rx - prev.Rx) * 8 / seconds, (tx - prev.Tx) * 8 / seconds);
        }

        rt.NetCounters = (rx, tx, now);
        return rates;
    }

    private static async Task UpsertInfoAsync(NetWatchDbContext db, int deviceId, ServerPollData s, DateTime now, CancellationToken ct)
    {
        var info = await db.ServerInfos.FirstOrDefaultAsync(i => i.DeviceId == deviceId, ct);
        if (info is null)
        {
            info = new ServerInfo { DeviceId = deviceId };
            db.ServerInfos.Add(info);
        }

        info.Method = s.Method;
        info.LastAttemptUtc = now;
        info.LastError = Trim(s.Error);
        if (s.Ok)
        {
            info.LastSuccessUtc = now;
            info.OsName = Trim(s.OsName, 200) ?? info.OsName;
            info.OsVersion = Trim(s.OsVersion, 100) ?? info.OsVersion;
            info.LastBootUtc = s.LastBootUtc ?? info.LastBootUtc;
            info.PendingReboot = s.PendingReboot ?? info.PendingReboot;
            info.LastPatchUtc = s.LastPatchUtc ?? info.LastPatchUtc;
            info.LastPatchId = Trim(s.LastPatchId, 64) ?? info.LastPatchId;
            info.CpuCount = s.CpuCount ?? info.CpuCount;
            info.TotalMemoryBytes = s.MemTotalBytes ?? info.TotalMemoryBytes;
            info.Load1 = s.Load1;
            info.Load5 = s.Load5;
            info.Load15 = s.Load15;
            info.EventLogError = Trim(s.EventLogError);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task VolumesAsync(NetWatchDbContext db, DeviceSnapshot device, ServerRuntime rt, ServerPollData server, DateTime now, long tsMs, CancellationToken ct)
    {
        if (server.Volumes.Count == 0)
        {
            return;
        }

        // Volume rows are refreshed at most every 5 minutes (or when a new volume appears); signals and series every poll.
        var newVolume = server.Volumes.Any(v => !rt.Volumes.ContainsKey(v.Name));
        if (newVolume || now - rt.LastVolumePersistUtc >= TimeSpan.FromMinutes(5))
        {
            var rows = await db.Volumes.Where(v => v.DeviceId == device.Id).ToListAsync(ct);
            foreach (var v in server.Volumes)
            {
                var row = rows.FirstOrDefault(r => string.Equals(r.Name, v.Name, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    row = new Volume { DeviceId = device.Id, Name = Trim(v.Name, 260)! };
                    db.Volumes.Add(row);
                    rows.Add(row);
                }

                row.Label = Trim(v.Label, 200);
                row.FileSystem = Trim(v.FileSystem, 32);
                row.TotalBytes = v.TotalBytes;
                row.FreeBytes = v.FreeBytes;
                row.LastSeenUtc = now;
            }

            // Volumes not seen for a day (removed disks, unmounted shares) are dropped.
            db.Volumes.RemoveRange(rows.Where(r => now - r.LastSeenUtc > TimeSpan.FromDays(1)));
            await db.SaveChangesAsync(ct);
            rt.Volumes = rows.Where(r => r.Id != 0).ToDictionary(r => r.Name, r => r.Id, StringComparer.OrdinalIgnoreCase);
            rt.LastVolumePersistUtc = now;
        }

        foreach (var v in server.Volumes)
        {
            _alerts.Publish(new MetricSignal(device.Id, null, MetricKeys.DiskUsed, v.Name, v.UsedPct, now, v.Name));
            var freeGb = Math.Round(v.FreeBytes / 1073741824.0, 2);
            _alerts.Publish(new MetricSignal(device.Id, null, MetricKeys.DiskFreeGb, v.Name, freeGb, now, v.Name));
            Series(device.Id, MetricKeys.DiskUsed, v.Name, v.UsedPct, tsMs);
        }
    }

    private async Task ServicesAsync(NetWatchDbContext db, DeviceSnapshot device, ServerRuntime rt, ServerPollData server, List<DeviceEvent> deviceEvents, DateTime now, CancellationToken ct)
    {
        if (server.Services.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var s in server.Services)
        {
            if (!rt.Services.TryGetValue(s.Name, out var known))
            {
                // The watched list changed since the runtime was loaded.
                rt.Services = (await db.WatchedServices.AsNoTracking().Where(w => w.DeviceId == device.Id).ToListAsync(ct))
                    .ToDictionary(w => w.ServiceName, w => (w.Id, w.LastState), StringComparer.OrdinalIgnoreCase);
                if (!rt.Services.TryGetValue(s.Name, out known))
                {
                    continue;
                }
            }

            var problem = s.State != ServiceState.Running;
            var label = s.DisplayName is { Length: > 0 } dn && !dn.Equals(s.Name, StringComparison.OrdinalIgnoreCase) ? $"{dn} ({s.Name})" : s.Name;
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.ServiceDown, s.Name, problem,
                problem ? $"Service {label} is {StateText(s.State)}" : $"Service {label} is running", now, s.Name));

            if (known.State != s.State)
            {
                if (known.State != ServiceState.Unknown)
                {
                    deviceEvents.Add(new DeviceEvent
                    {
                        DeviceId = device.Id,
                        TimestampUtc = now,
                        Kind = DeviceEventKind.Service,
                        Severity = problem ? Severity.Major : Severity.Info,
                        Message = $"Service {label} changed from {StateText(known.State)} to {StateText(s.State)}",
                    });
                }

                await db.WatchedServices.Where(w => w.Id == known.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(w => w.LastState, s.State)
                    .SetProperty(w => w.DisplayName, s.DisplayName)
                    .SetProperty(w => w.StartMode, s.StartMode)
                    .SetProperty(w => w.LastChangeUtc, now)
                    .SetProperty(w => w.LastSeenUtc, now), ct);
                rt.Services[s.Name] = (known.Id, s.State);
                changed = true;
            }
        }

        if (!changed && server.Services.Count > 0)
        {
            // Keep "last seen" fresh without rewriting on every poll.
            var stale = now.AddMinutes(-10);
            await db.WatchedServices.Where(w => w.DeviceId == device.Id && (w.LastSeenUtc == null || w.LastSeenUtc < stale))
                .ExecuteUpdateAsync(u => u.SetProperty(w => w.LastSeenUtc, now), ct);
        }
    }

    internal static string StateText(ServiceState s) => s switch
    {
        ServiceState.Running => "running",
        ServiceState.Stopped => "stopped",
        ServiceState.StartPending => "starting",
        ServiceState.StopPending => "stopping",
        ServiceState.Paused => "paused",
        ServiceState.Failed => "failed",
        ServiceState.NotFound => "not installed",
        _ => "unknown",
    };

    private async Task WindowsEventsAsync(EventsDbContext events, DeviceSnapshot device, ServerRuntime rt, ServerPollData server, Dictionary<string, long> bookmarks, DateTime now, CancellationToken ct)
    {
        foreach (var e in server.Events.OrderBy(e => e.TimestampUtc))
        {
            if (rt.Bookmarks.TryGetValue(e.LogName, out var last) && e.RecordId <= last)
            {
                continue;
            }

            events.WindowsEvents.Add(new WindowsEventEntry
            {
                DeviceId = device.Id,
                LogName = e.LogName,
                RecordId = e.RecordId,
                TimestampUtc = e.TimestampUtc,
                Level = e.Level,
                EventId = e.EventId,
                Provider = Trim(e.Provider, 200),
                Message = e.Message,
            });
            _alerts.Publish(new EventSignal(device.Id, EventKeys.WindowsEvent,
                $"{e.LogName} event {e.EventId} from {e.Provider}: {e.Message}", e.TimestampUtc,
                SeverityMapping.FromWindowsLevel(e.Level), e.Provider, e.EventId));
        }

        foreach (var (log, recordId) in bookmarks)
        {
            var existing = await events.EventBookmarks.FirstOrDefaultAsync(b => b.DeviceId == device.Id && b.LogName == log, ct);
            if (existing is null)
            {
                events.EventBookmarks.Add(new EventBookmark { DeviceId = device.Id, LogName = log, LastRecordId = recordId, UpdatedUtc = now });
            }
            else
            {
                existing.LastRecordId = recordId;
                existing.UpdatedUtc = now;
            }

            rt.Bookmarks[log] = recordId;
        }

        try
        {
            await events.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A duplicate record (e.g. the log was cleared and record numbers restarted) must not block the bookmark.
            _logger.LogWarning(ex, "Could not store Windows events of {Device}", device.Name);
            events.ChangeTracker.Clear();
        }
    }

    private async Task<ServerRuntime> GetRuntimeAsync(int deviceId, CancellationToken ct)
    {
        var rt = _runtime.GetOrAdd(deviceId, _ => new ServerRuntime());
        if (rt.Loaded)
        {
            return rt;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        rt.Bookmarks = await events.EventBookmarks.AsNoTracking().Where(b => b.DeviceId == deviceId)
            .ToDictionaryAsync(b => b.LogName, b => b.LastRecordId, StringComparer.OrdinalIgnoreCase, ct);
        rt.Services = (await db.WatchedServices.AsNoTracking().Where(w => w.DeviceId == deviceId).ToListAsync(ct))
            .ToDictionary(w => w.ServiceName, w => (w.Id, w.LastState), StringComparer.OrdinalIgnoreCase);
        rt.Volumes = await db.Volumes.AsNoTracking().Where(v => v.DeviceId == deviceId)
            .ToDictionaryAsync(v => v.Name, v => v.Id, StringComparer.OrdinalIgnoreCase, ct);
        rt.PendingReboot = await db.ServerInfos.AsNoTracking().Where(i => i.DeviceId == deviceId).Select(i => i.PendingReboot).FirstOrDefaultAsync(ct);
        rt.Loaded = true;
        return rt;
    }

    private static string? Trim(string? s, int max = 1000) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s.Trim() : s[..max];
}
