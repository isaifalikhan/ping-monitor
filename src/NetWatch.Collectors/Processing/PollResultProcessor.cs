using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Live;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Snmp;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Metrics;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Config;
using NetWatch.Data.Events;

namespace NetWatch.Collectors.Processing;

/// <summary>Live update payload sent when a device changes status.</summary>
public sealed record DeviceStatusChange(int DeviceId, string Name, DeviceStatus Status, DateTime SinceUtc);

/// <summary>Live update payload sent to the "device:{id}" group after each poll.</summary>
public sealed record DeviceMetricsUpdate(
    int DeviceId,
    long Ts,
    DeviceStatus Status,
    double? RttMs,
    double? LossPct,
    double? CpuPct,
    double? MemPct,
    double? TempC,
    IReadOnlyList<InterfaceMetricsUpdate> Interfaces);

public sealed record InterfaceMetricsUpdate(int Id, int OperStatus, double? InBps, double? OutBps, double? InUtilPct, double? OutUtilPct);

public static class LiveEvents
{
    public const string DeviceStatus = "deviceStatus";
    public const string DeviceMetrics = "deviceMetrics";
    public const string Summary = "summary";

    public static string DeviceGroup(int deviceId) => $"device:{deviceId}";
}

/// <summary>
/// Turns raw poll results into state: device status (Down after N failed polls), reboot detection, interface
/// inventory sync, counter → rate conversion, sensor tracking, metric samples, alert signals and live updates.
/// The scheduler guarantees a device is never processed concurrently with itself.
/// </summary>
public sealed class PollResultProcessor
{
    // sysUpTime is a 32-bit TimeTicks value (1/100 s) that wraps after ~497 days.
    private const long UptimeWrapSec = 42_949_672;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMetricStore _metrics;
    private readonly IAlertSignalSink _alerts;
    private readonly ILiveUpdatePublisher _live;
    private readonly LiveStateCache _liveCache;
    private readonly SettingsService _settings;
    private readonly CredentialResolver _credentials;
    private readonly ServerResultProcessor _servers;
    private readonly VmwareResultProcessor _vmware;
    private readonly DcResultProcessor _dc;
    private readonly HardwareResultProcessor? _hardware;
    private readonly TimeProvider _time;
    private readonly ILogger<PollResultProcessor> _logger;
    private readonly MacTableStore _macTables;
    private readonly ConcurrentDictionary<int, DeviceRuntime> _runtime = new();
    private readonly ConcurrentDictionary<int, CounterSnapshot> _counters = new();

    public PollResultProcessor(
        IServiceScopeFactory scopeFactory,
        IMetricStore metrics,
        IAlertSignalSink alerts,
        ILiveUpdatePublisher live,
        LiveStateCache liveCache,
        SettingsService settings,
        CredentialResolver credentials,
        ServerResultProcessor servers,
        VmwareResultProcessor vmware,
        DcResultProcessor dc,
        HardwareResultProcessor? hardware,
        MacTableStore macTables,
        TimeProvider time,
        ILogger<PollResultProcessor> logger)
    {
        _macTables = macTables;
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _alerts = alerts;
        _live = live;
        _liveCache = liveCache;
        _settings = settings;
        _credentials = credentials;
        _servers = servers;
        _vmware = vmware;
        _dc = dc;
        _hardware = hardware;
        _time = time;
        _logger = logger;
    }

    private sealed class DeviceRuntime
    {
        public DeviceStatus Status;
        public DateTime StatusSinceUtc;
        public int Failures;
        public long? LastUptimeSec;
        public bool ForceInterfaceRefresh = true;
        public DateTime LastInterfaceRefreshUtc = DateTime.MinValue;
        public DateTime LastInventoryRefreshUtc = DateTime.MinValue;
        public DateTime LastMacTableUtc = DateTime.MinValue;
        public Dictionary<int, InterfaceRuntime> ByIfIndex = new();
        public readonly Dictionary<string, SensorRuntime> Sensors = new(StringComparer.Ordinal);
    }

    private sealed class InterfaceRuntime
    {
        public int Id;
        public string Name = string.Empty;
        public string? Description;
        public long SpeedBps;
        public int OperStatus;
        public int AdminStatus;
        public bool IsMonitored;
        public int? Duplex;
        public long? ExpectedSpeedBps;
        public int PersistedFlaps;
        public bool FlapFlagged;
        public FlapTracker Flaps = new();
    }

    private sealed class SensorRuntime
    {
        public int Id;
        public SensorState State;
        public DateTime LastPersistUtc;
    }

    /// <summary>Loads persisted counter state so the first poll after a restart already yields rates.</summary>
    public async Task InitialiseAsync(CancellationToken ct)
    {
        foreach (var (id, snapshot) in await _metrics.LoadCounterStatesAsync(ct))
        {
            _counters[id] = snapshot;
        }

        _logger.LogInformation("Loaded {Count} interface counter states", _counters.Count);
    }

    public IReadOnlyCollection<KeyValuePair<int, CounterSnapshot>> GetCounterStates() => _counters.ToArray();

    /// <summary>Drops runtime state of a device that was deleted.</summary>
    public void Forget(int deviceId)
    {
        if (_runtime.TryRemove(deviceId, out var rt))
        {
            foreach (var ifr in rt.ByIfIndex.Values)
            {
                _counters.TryRemove(ifr.Id, out _);
            }
        }

        _liveCache.RemoveDevice(deviceId);
        _servers.Forget(deviceId);
        _dc.Forget(deviceId);
        _hardware?.Forget(deviceId);
        _macTables.Forget(deviceId);
        try
        {
            _metrics.ForgetDevice(deviceId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not remove metric series of device {DeviceId}", deviceId);
        }
    }

    public async Task<PollRequest> PrepareAsync(DeviceSnapshot device, CancellationToken ct)
    {
        var rt = await GetRuntimeAsync(device.Id, ct);
        var settings = _settings.Monitoring;
        var now = _time.GetUtcNow().UtcDateTime;
        var snmp = device.SnmpCredentialId is { } credentialId ? await _credentials.GetSnmpAsync(credentialId, ct) : null;
        var hasSnmp = snmp is not null;
        var refreshInterfaces = hasSnmp &&
            (rt.ForceInterfaceRefresh || now - rt.LastInterfaceRefreshUtc >= TimeSpan.FromMinutes(settings.InterfaceMetadataRefreshMin));
        var refreshInventory = hasSnmp && now - rt.LastInventoryRefreshUtc >= TimeSpan.FromHours(settings.InventoryRefreshHours);
        var server = await _servers.PrepareAsync(device, ct);
        var vmware = await _vmware.PrepareAsync(device, ct);
        var dc = await _dc.PrepareAsync(device, ct);
        var hardware = _hardware is null ? null : await _hardware.PrepareAsync(device, ct);

        var readNeighbors = refreshInterfaces && settings.DiscoverNeighbors && device.Type is DeviceType.Switch or DeviceType.Router or DeviceType.Firewall or DeviceType.Other;
        // Learned MAC addresses per port (and ARP on routed switches) every 15 minutes.
        var readMacTable = hasSnmp && device.Type is DeviceType.Switch or DeviceType.Router or DeviceType.Other &&
            now - rt.LastMacTableUtc >= MacTableStore.RefreshInterval;
        return new PollRequest(device, snmp, refreshInterfaces, refreshInventory, settings.PingCount, settings.PingTimeoutMs, settings.SnmpTimeoutMs, settings.SnmpRetries,
            server, hasSnmp && server is null && ServerResultProcessor.WantsSnmpStorage(device), vmware, dc, readNeighbors, hardware, readMacTable);
    }

    public async Task ProcessAsync(PollRequest request, DevicePollResult result, CancellationToken ct)
    {
        var device = request.Device;
        var rt = await GetRuntimeAsync(device.Id, ct);
        var settings = _settings.Monitoring;
        var now = result.TimestampUtc;
        var tsMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        var deviceEvents = new List<DeviceEvent>();

        // ---- availability -------------------------------------------------------------
        // A server that blocks ICMP but answers WinRM/SSH is reachable too.
        var pingOrSnmp = result.PingOk || result.SnmpOk == true;
        var reachable = pingOrSnmp || result.Server is { Ok: true, Method: not ServerCollectionMethod.Snmp };
        _liveCache.SetReachable(device.Id, reachable);
        DeviceStatus newStatus;
        if (reachable)
        {
            rt.Failures = 0;
            newStatus = result.SnmpOk == false || (result.LossPct ?? 0) >= 20 || (result.Server is { Ok: false } && pingOrSnmp)
                ? DeviceStatus.Warning
                : DeviceStatus.Up;
        }
        else
        {
            rt.Failures++;
            newStatus = rt.Failures >= settings.DownAfterFailedPolls ? DeviceStatus.Down : rt.Status;
        }

        var statusChanged = newStatus != rt.Status;
        if (statusChanged)
        {
            if (rt.Status != DeviceStatus.Unknown || newStatus == DeviceStatus.Down)
            {
                deviceEvents.Add(new DeviceEvent
                {
                    DeviceId = device.Id,
                    TimestampUtc = now,
                    Kind = DeviceEventKind.StatusChange,
                    Severity = newStatus switch { DeviceStatus.Down => Severity.Critical, DeviceStatus.Warning => Severity.Warning, _ => Severity.Info },
                    Message = $"Status changed from {rt.Status} to {newStatus}" + (newStatus == DeviceStatus.Down ? $" after {rt.Failures} failed polls" : string.Empty),
                });
            }

            rt.Status = newStatus;
            rt.StatusSinceUtc = now;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var eventsDb = scope.ServiceProvider.GetRequiredService<EventsDbContext>();

        // ---- servers (before device metrics: fills CPU/memory/uptime when SNMP does not) --
        if (result.Server is not null)
        {
            try
            {
                await _servers.ProcessAsync(device, result, pingOrSnmp, db, eventsDb, deviceEvents, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Processing server data of {Device} failed", device.Name);
            }
        }

        // ---- VMware (vCenter / ESXi API) --------------------------------------------------
        if (result.Vmware is not null)
        {
            try
            {
                await _vmware.ProcessAsync(device, result, db, eventsDb, deviceEvents, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Processing VMware data of {Device} failed", device.Name);
                db.ChangeTracker.Clear();
            }
        }

        // ---- GPUs and server hardware (iLO / iDRAC) ------------------------------------------
        if (_hardware is not null && (result.Hardware is not null || result.Server is { GpusRead: true }))
        {
            try
            {
                if (result.Server is { } srv)
                {
                    await _hardware.ProcessGpusAsync(device, srv, db, now, ct);
                }

                if (result.Hardware is { } hw)
                {
                    await _hardware.ProcessHardwareAsync(device, hw, db, now, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Processing hardware/GPU data of {Device} failed", device.Name);
                db.ChangeTracker.Clear();
            }
        }

        // ---- domain controllers (Security log, replication, clock) ------------------------
        if (result.Dc is not null)
        {
            try
            {
                await _dc.ProcessAsync(device, result, db, eventsDb, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Processing DC data of {Device} failed", device.Name);
                db.ChangeTracker.Clear();
                eventsDb.ChangeTracker.Clear();
            }
        }

        // ---- reboot detection ---------------------------------------------------------
        var rebooted = false;
        if (result.UptimeSec is { } uptime)
        {
            if (rt.LastUptimeSec is { } previous && uptime + 60 < previous && previous < UptimeWrapSec - 86_400)
            {
                rebooted = true;
                var message = $"Device rebooted (uptime went from {FormatUptime(previous)} to {FormatUptime(uptime)}).";
                deviceEvents.Add(new DeviceEvent { DeviceId = device.Id, TimestampUtc = now, Kind = DeviceEventKind.Reboot, Severity = Severity.Warning, Message = message });
                _alerts.Publish(new EventSignal(device.Id, EventKeys.DeviceReboot, message, now));
            }

            rt.LastUptimeSec = uptime;
        }

        // ---- inventory / interface table ----------------------------------------------
        if (result.InterfaceMetadata is { } metadata)
        {
            await SyncInterfacesAsync(db, device.Id, rt, metadata, now, ct);
            rt.LastInterfaceRefreshUtc = now;
            rt.ForceInterfaceRefresh = false;
        }

        if (result.Inventory is { } inventory)
        {
            await UpdateInventoryAsync(db, device.Id, inventory, deviceEvents, now, ct);
            rt.LastInventoryRefreshUtc = now;
        }

        if (result.Neighbors is { } neighbors)
        {
            await SyncNeighborsAsync(db, device.Id, rt, neighbors, now, ct);
        }

        if (result.MacTable is { } macTable)
        {
            var entries = macTable.Entries.Select(e => rt.ByIfIndex.TryGetValue(e.IfIndex, out var port)
                    ? new MacPortEntry(e.Mac, e.Vlan, e.IfIndex, port.Id, port.Name, e.Static)
                    : new MacPortEntry(e.Mac, e.Vlan, e.IfIndex, null, MacTableStore.PortLabel(e.IfIndex), e.Static))
                .ToList();
            _macTables.Update(device.Id, device.Name, now, entries, macTable.Arp);
            rt.LastMacTableUtc = now;
        }

        // ---- interfaces -----------------------------------------------------------------
        var interfaceUpdates = new List<InterfaceMetricsUpdate>(result.Interfaces.Count);
        var statusUpdates = new List<(int Id, int Oper, int Admin)>();
        var portUpdates = new List<(int Id, int? Duplex, int Flaps)>();
        var flapWindow = TimeSpan.FromMinutes(settings.FlapWindowMin);
        foreach (var c in result.Interfaces)
        {
            if (!rt.ByIfIndex.TryGetValue(c.IfIndex, out var ifr))
            {
                // A new interface appeared: re-read the interface table next cycle.
                rt.ForceInterfaceRefresh = true;
                continue;
            }

            var snapshot = new CounterSnapshot(tsMs, c.InOctets, c.OutOctets, c.InErrors, c.OutErrors, c.InDiscards, c.OutDiscards, c.HighCapacity);
            InterfaceRates? rates = _counters.TryGetValue(ifr.Id, out var prev)
                ? CounterMath.ComputeRates(prev, snapshot, ifr.SpeedBps, rebooted)
                : null;
            _counters[ifr.Id] = snapshot;

            var important = device.ImportantInterfaceIds.Contains(ifr.Id);
            var cameUp = ifr.OperStatus != 0 && ifr.OperStatus != 1 && c.OperStatus == 1;
            if (c.OperStatus != ifr.OperStatus || c.AdminStatus != ifr.AdminStatus)
            {
                if (ifr.OperStatus != 0 && important)
                {
                    deviceEvents.Add(new DeviceEvent
                    {
                        DeviceId = device.Id,
                        TimestampUtc = now,
                        Kind = DeviceEventKind.InterfaceChange,
                        Severity = c.OperStatus == 1 ? Severity.Info : Severity.Major,
                        Message = $"Interface {ifr.Name} is now {IfStatusText(c.OperStatus)} (admin {IfStatusText(c.AdminStatus)})",
                    });
                }

                statusUpdates.Add((ifr.Id, c.OperStatus, c.AdminStatus));
                ifr.OperStatus = c.OperStatus;
                ifr.AdminStatus = c.AdminStatus;
            }

            var problem = c.AdminStatus == 1 && c.OperStatus is not (1 or 5);
            _alerts.Publish(new StateSignal(device.Id, ifr.Id, StateKeys.InterfaceDown, null, problem,
                problem ? $"Interface {ifr.Name} is {IfStatusText(c.OperStatus)}" : $"Interface {ifr.Name} is up",
                now, ifr.Name, important));

            // ---- link flapping (ifOperStatus changes plus ifLastChange moves between polls)
            ifr.Flaps.Observe(now, c.OperStatus, c.LastChangeTicks, rebooted, flapWindow);
            var flapping = ifr.Flaps.InWindow >= settings.FlapChanges;
            if (flapping || ifr.FlapFlagged)
            {
                _alerts.Publish(new StateSignal(device.Id, ifr.Id, StateKeys.InterfaceFlapping, null, flapping,
                    flapping
                        ? $"Interface {ifr.Name} changed state {ifr.Flaps.InWindow} times in {settings.FlapWindowMin} min ({ifr.Flaps.Last24h} in 24 h)"
                        : $"Interface {ifr.Name} is stable again",
                    now, ifr.Name, important));
                ifr.FlapFlagged = flapping;
            }

            var duplexChanged = c.Duplex is { } dx && dx != ifr.Duplex;
            if (duplexChanged)
            {
                ifr.Duplex = c.Duplex;
            }

            if (duplexChanged || ifr.Flaps.Last24h != ifr.PersistedFlaps)
            {
                portUpdates.Add((ifr.Id, ifr.Duplex, ifr.Flaps.Last24h));
                ifr.PersistedFlaps = ifr.Flaps.Last24h;
            }

            // ---- duplex and negotiated speed of important ports
            if (important)
            {
                var half = PortChecks.IsHalfDuplex(c.OperStatus, ifr.Duplex);
                _alerts.Publish(new StateSignal(device.Id, ifr.Id, StateKeys.InterfaceHalfDuplex, null, half,
                    half ? $"Interface {ifr.Name} is running half duplex" : $"Interface {ifr.Name} duplex is {PortChecks.DuplexText(ifr.Duplex)}", now, ifr.Name, true));
                var slow = PortChecks.IsBelowExpectedSpeed(c.OperStatus, ifr.SpeedBps, ifr.ExpectedSpeedBps);
                _alerts.Publish(new StateSignal(device.Id, ifr.Id, StateKeys.InterfaceSpeedLow, null, slow,
                    slow
                        ? $"Interface {ifr.Name} runs at {PortChecks.SpeedText(ifr.SpeedBps)}, expected {PortChecks.SpeedText(ifr.ExpectedSpeedBps!.Value)}"
                        : $"Interface {ifr.Name} speed is as expected",
                    now, ifr.Name, true));

                // A link that just came up may have renegotiated: re-read speeds next cycle.
                if (cameUp)
                {
                    rt.ForceInterfaceRefresh = true;
                }
            }

            if (!ifr.IsMonitored)
            {
                continue;
            }

            _metrics.Enqueue(new InterfaceSample(ifr.Id, tsMs, rates?.InBps, rates?.OutBps, rates?.InUtilPct, rates?.OutUtilPct,
                rates?.InErrPs, rates?.OutErrPs, rates?.InDiscPs, rates?.OutDiscPs, c.OperStatus == 1));

            if (rates is { } r)
            {
                if (r.InUtilPct.HasValue || r.OutUtilPct.HasValue)
                {
                    _alerts.Publish(new MetricSignal(device.Id, ifr.Id, MetricKeys.InterfaceUtilization, null,
                        Math.Max(r.InUtilPct ?? 0, r.OutUtilPct ?? 0), now, ifr.Name, important));
                }

                if (r.InErrPs.HasValue || r.OutErrPs.HasValue)
                {
                    _alerts.Publish(new MetricSignal(device.Id, ifr.Id, MetricKeys.InterfaceErrors, null,
                        (r.InErrPs ?? 0) + (r.OutErrPs ?? 0), now, ifr.Name, important));
                }
            }

            _liveCache.Set(new InterfaceLive(ifr.Id, device.Id, c.IfIndex, ifr.Name, c.OperStatus, c.AdminStatus, ifr.SpeedBps,
                rates?.InBps, rates?.OutBps, rates?.InUtilPct, rates?.OutUtilPct,
                rates is null ? null : (rates.Value.InErrPs ?? 0) + (rates.Value.OutErrPs ?? 0), now));
            interfaceUpdates.Add(new InterfaceMetricsUpdate(ifr.Id, c.OperStatus, rates?.InBps, rates?.OutBps, rates?.InUtilPct, rates?.OutUtilPct));
        }

        // ---- sensors ------------------------------------------------------------------
        double? maxTemp = null;
        foreach (var s in result.Sensors)
        {
            if (s.Kind == SensorKind.Temperature && s.Value is { } t)
            {
                maxTemp = maxTemp is null ? t : Math.Max(maxTemp.Value, t);
            }

            await TrackSensorAsync(db, device.Id, rt, s, deviceEvents, now, ct);
            var problem = s.State is SensorState.Warning or SensorState.Critical;
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.SensorProblem, $"{s.Kind}:{s.SourceKey}", problem,
                problem ? $"{s.Kind} '{s.Name}' reports {s.State}" : $"{s.Kind} '{s.Name}' is OK", now, s.Name));
        }

        // ---- device-level metrics and signals -----------------------------------------
        var up = newStatus is DeviceStatus.Up or DeviceStatus.Warning;
        _metrics.Enqueue(new DeviceSample(device.Id, tsMs, up, result.PingOk ? result.RttMs : null, result.LossPct, result.CpuPct, result.MemPct, maxTemp));

        _alerts.Publish(new StateSignal(device.Id, null, StateKeys.DeviceDown, null, newStatus == DeviceStatus.Down,
            newStatus == DeviceStatus.Down ? $"{device.Name} ({device.IpAddress}) is not responding to ping or SNMP" : $"{device.Name} is reachable again",
            now));

        if (result.SnmpOk is { } snmpOk && reachable)
        {
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.SnmpUnreachable, null, !snmpOk,
                snmpOk ? "SNMP is responding" : $"SNMP is not responding: {result.SnmpError}", now));
        }

        if (reachable)
        {
            if (result.PingOk && result.RttMs is { } rtt)
            {
                _alerts.Publish(new MetricSignal(device.Id, null, MetricKeys.PingRtt, null, rtt, now));
            }

            if (result.LossPct is { } loss)
            {
                _alerts.Publish(new MetricSignal(device.Id, null, MetricKeys.PingLoss, null, loss, now));
            }

            Publish(MetricKeys.Cpu, result.CpuPct);
            Publish(MetricKeys.Memory, result.MemPct);
            Publish(MetricKeys.Temperature, maxTemp);
        }

        // ---- persistence ----------------------------------------------------------------
        await db.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, rt.Status)
            .SetProperty(d => d.StatusSinceUtc, rt.StatusSinceUtc)
            .SetProperty(d => d.LastPollUtc, now)
            .SetProperty(d => d.ConsecutiveFailures, rt.Failures)
            .SetProperty(d => d.LastUptimeSec, rt.LastUptimeSec), ct);

        foreach (var (id, duplex, flaps) in portUpdates)
        {
            await db.Interfaces.Where(i => i.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Duplex, duplex)
                .SetProperty(i => i.FlapCount24h, flaps), ct);
        }

        foreach (var (id, oper, admin) in statusUpdates)
        {
            await db.Interfaces.Where(i => i.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.OperStatus, oper)
                .SetProperty(i => i.AdminStatus, admin)
                .SetProperty(i => i.LastChangeUtc, now), ct);
        }

        if (deviceEvents.Count > 0)
        {
            eventsDb.DeviceEvents.AddRange(deviceEvents);
            await eventsDb.SaveChangesAsync(ct);
        }

        // ---- live updates -------------------------------------------------------------
        _liveCache.Set(new DeviceLive(device.Id, rt.Status, now, result.PingOk ? result.RttMs : null, result.LossPct,
            result.CpuPct, result.MemPct, maxTemp, result.UptimeSec, result.Duration.TotalMilliseconds));

        if (statusChanged)
        {
            await _live.PublishAsync(LiveEvents.DeviceStatus, new DeviceStatusChange(device.Id, device.Name, rt.Status, rt.StatusSinceUtc));
        }

        await _live.PublishAsync(
            LiveEvents.DeviceMetrics,
            new DeviceMetricsUpdate(device.Id, tsMs, rt.Status, result.PingOk ? result.RttMs : null, result.LossPct, result.CpuPct, result.MemPct, maxTemp, interfaceUpdates),
            LiveEvents.DeviceGroup(device.Id));

        void Publish(string key, double? value)
        {
            if (value is { } v)
            {
                _alerts.Publish(new MetricSignal(device.Id, null, key, null, v, now));
            }
        }
    }

    private async Task<DeviceRuntime> GetRuntimeAsync(int deviceId, CancellationToken ct)
    {
        if (_runtime.TryGetValue(deviceId, out var existing))
        {
            return existing;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var device = await db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => new { d.Status, d.StatusSinceUtc, d.ConsecutiveFailures, d.LastUptimeSec, d.Vendor, d.Model })
            .FirstOrDefaultAsync(ct);
        var interfaces = await db.Interfaces.AsNoTracking().Where(i => i.DeviceId == deviceId).ToListAsync(ct);
        var sensors = await db.Sensors.AsNoTracking().Where(s => s.DeviceId == deviceId).ToListAsync(ct);
        var now = _time.GetUtcNow().UtcDateTime;

        var rt = new DeviceRuntime
        {
            Status = device?.Status ?? DeviceStatus.Unknown,
            StatusSinceUtc = device?.StatusSinceUtc ?? now,
            Failures = device?.ConsecutiveFailures ?? 0,
            LastUptimeSec = device?.LastUptimeSec,
            ByIfIndex = BuildInterfaceMap(interfaces),

            // Inventory is re-read on the first poll only if it was never collected.
            LastInventoryRefreshUtc = device?.Vendor is null && device?.Model is null ? DateTime.MinValue : now,
        };

        foreach (var s in sensors)
        {
            rt.Sensors[$"{s.Kind}:{s.SourceKey}"] = new SensorRuntime { Id = s.Id, State = s.LastState, LastPersistUtc = s.LastSeenUtc };
        }

        return _runtime.GetOrAdd(deviceId, rt);
    }

    private static Dictionary<int, InterfaceRuntime> BuildInterfaceMap(IEnumerable<NetInterface> interfaces) =>
        interfaces.ToDictionary(i => i.IfIndex, i => new InterfaceRuntime
        {
            Id = i.Id,
            Name = i.Name,
            Description = i.Description,
            SpeedBps = i.SpeedBps,
            OperStatus = i.OperStatus,
            AdminStatus = i.AdminStatus,
            IsMonitored = i.IsMonitored,
            Duplex = i.Duplex,
            ExpectedSpeedBps = i.ExpectedSpeedBps,
            PersistedFlaps = i.FlapCount24h,
        });

    /// <summary>
    /// Stores the LLDP/CDP neighbours of a device: one row per local port and neighbour, matched to monitored
    /// devices by management IP, then name (short name of an FQDN, or sysName). Rows not seen for a day are removed.
    /// </summary>
    private static async Task SyncNeighborsAsync(NetWatchDbContext db, int deviceId, DeviceRuntime rt, IReadOnlyList<NeighborReading> neighbors, DateTime now, CancellationToken ct)
    {
        var interfaces = rt.ByIfIndex.ToDictionary(kv => kv.Key, kv => (kv.Value.Name, kv.Value.Description));
        var ips = neighbors.Select(n => n.RemoteAddress).Where(a => a is not null).Distinct().ToList();
        var names = neighbors.Select(n => NeighborParser.ShortName(n.RemoteName)).Where(n => n is not null).Distinct().ToList();
        var candidates = await db.Devices.AsNoTracking()
            .Where(d => d.Id != deviceId && (ips.Contains(d.IpAddress) || names.Contains(d.Name.ToUpper()) || (d.SysName != null && names.Contains(d.SysName.ToUpper()))))
            .Select(d => new { d.Id, d.IpAddress, d.Name, d.SysName })
            .ToListAsync(ct);

        int? Match(NeighborReading n)
        {
            if (n.RemoteAddress is { } ip && candidates.FirstOrDefault(c => c.IpAddress == ip) is { } byIp)
            {
                return byIp.Id;
            }

            var shortName = NeighborParser.ShortName(n.RemoteName);
            return shortName is null ? null : candidates.FirstOrDefault(c => NeighborParser.ShortName(c.Name) == shortName || NeighborParser.ShortName(c.SysName) == shortName)?.Id;
        }

        var existing = await db.NeighborLinks.Where(l => l.DeviceId == deviceId).ToListAsync(ct);
        foreach (var n in neighbors)
        {
            var ifIndex = NeighborParser.ResolveLocalIfIndex(n, interfaces);
            var local = ifIndex is { } idx && rt.ByIfIndex.TryGetValue(idx, out var ifr) ? ifr : null;
            var localPort = local?.Name ?? n.LocalPortId ?? n.LocalPortDesc ?? n.LocalPortNum.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var remoteKey = NeighborParser.ShortName(n.RemoteName) ?? n.RemoteChassisId ?? n.RemoteAddress;
            var row = existing.FirstOrDefault(l => l.LocalPort == localPort &&
                (NeighborParser.ShortName(l.RemoteName) ?? l.RemoteChassisId ?? l.RemoteAddress) == remoteKey);
            if (row is null)
            {
                row = new NeighborLink { DeviceId = deviceId, FirstSeenUtc = now };
                db.NeighborLinks.Add(row);
                existing.Add(row);
            }

            row.LocalInterfaceId = local?.Id;
            row.LocalPort = localPort.Length > 255 ? localPort[..255] : localPort;
            row.Protocol = n.Protocol;
            row.RemoteName = n.RemoteName;
            row.RemoteChassisId = n.RemoteChassisId;
            row.RemotePort = n.RemotePort;
            row.RemotePortDescription = n.RemotePortDesc;
            row.RemoteAddress = n.RemoteAddress;
            row.RemotePlatform = n.RemotePlatform;
            row.RemoteDeviceId = Match(n);
            row.LastSeenUtc = now;
        }

        db.NeighborLinks.RemoveRange(existing.Where(l => now - l.LastSeenUtc > TimeSpan.FromDays(1)));
        await db.SaveChangesAsync(ct);
    }

    private static async Task SyncInterfacesAsync(NetWatchDbContext db, int deviceId, DeviceRuntime rt, IReadOnlyList<InterfaceMetadata> metadata, DateTime now, CancellationToken ct)
    {
        var existing = await db.Interfaces.Where(i => i.DeviceId == deviceId).ToListAsync(ct);
        var byIndex = existing.ToDictionary(i => i.IfIndex);
        foreach (var m in metadata)
        {
            if (byIndex.TryGetValue(m.IfIndex, out var row))
            {
                row.Name = m.Name;
                row.Description = m.Description;
                row.Alias = m.Alias;
                row.IfType = m.IfType;
                row.SpeedBps = m.SpeedBps;
                row.Mac = m.Mac;
                row.LastSeenUtc = now;
            }
            else
            {
                row = new NetInterface
                {
                    DeviceId = deviceId,
                    IfIndex = m.IfIndex,
                    Name = m.Name,
                    Description = m.Description,
                    Alias = m.Alias,
                    IfType = m.IfType,
                    SpeedBps = m.SpeedBps,
                    Mac = m.Mac,

                    // Software loopback (24) carries no useful traffic.
                    IsMonitored = m.IfType != 24,
                    LastSeenUtc = now,
                };
                db.Interfaces.Add(row);
                existing.Add(row);
            }
        }

        await db.SaveChangesAsync(ct);

        // Keep the live oper/admin status we already know; take everything else from the database.
        var previous = rt.ByIfIndex;
        var map = BuildInterfaceMap(existing);
        foreach (var (index, ifr) in map)
        {
            if (previous.TryGetValue(index, out var old))
            {
                ifr.OperStatus = old.OperStatus;
                ifr.AdminStatus = old.AdminStatus;
                ifr.Flaps = old.Flaps;
                ifr.FlapFlagged = old.FlapFlagged;
                ifr.Duplex = old.Duplex ?? ifr.Duplex;
                ifr.PersistedFlaps = old.PersistedFlaps;
            }
        }

        rt.ByIfIndex = map;
    }

    private static async Task UpdateInventoryAsync(NetWatchDbContext db, int deviceId, DeviceInventoryInfo info, List<DeviceEvent> events, DateTime now, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null)
        {
            return;
        }

        var changes = new List<string>();
        Apply(device.Vendor, info.Vendor, v => device.Vendor = v, "vendor");
        Apply(device.Model, info.Model, v => device.Model = v, "model");
        Apply(device.SerialNumber, info.SerialNumber, v => device.SerialNumber = v, "serial");
        Apply(device.Firmware, info.Firmware, v => device.Firmware = v, "firmware");
        Apply(device.SysObjectId, info.SysObjectId, v => device.SysObjectId = v, null);
        Apply(device.SysName, info.SysName, v => device.SysName = v, null);
        Apply(device.SysDescription, info.SysDescription, v => device.SysDescription = v, null);
        if (string.IsNullOrWhiteSpace(device.Location) && !string.IsNullOrWhiteSpace(info.Location))
        {
            device.Location = info.Location;
        }

        if (db.ChangeTracker.HasChanges())
        {
            device.UpdatedUtc = now;
            await db.SaveChangesAsync(ct);
        }

        if (changes.Count > 0)
        {
            events.Add(new DeviceEvent { DeviceId = deviceId, TimestampUtc = now, Kind = DeviceEventKind.Inventory, Severity = Severity.Info, Message = "Inventory updated: " + string.Join(", ", changes) });
        }

        void Apply(string? current, string? value, Action<string> set, string? label)
        {
            if (string.IsNullOrWhiteSpace(value) || value == current)
            {
                return;
            }

            set(value.Length > 500 ? value[..500] : value);
            if (label is not null)
            {
                changes.Add(current is null ? $"{label}={value}" : $"{label} {current} → {value}");
            }
        }
    }

    private static async Task TrackSensorAsync(NetWatchDbContext db, int deviceId, DeviceRuntime rt, SensorReading s, List<DeviceEvent> events, DateTime now, CancellationToken ct)
    {
        var key = $"{s.Kind}:{s.SourceKey}";
        if (!rt.Sensors.TryGetValue(key, out var sr))
        {
            var entity = new Sensor
            {
                DeviceId = deviceId,
                Kind = s.Kind,
                SourceKey = s.SourceKey,
                Name = s.Name,
                Unit = s.Unit,
                LastValue = s.Value,
                LastState = s.State,
                LastSeenUtc = now,
            };
            db.Sensors.Add(entity);
            await db.SaveChangesAsync(ct);
            rt.Sensors[key] = new SensorRuntime { Id = entity.Id, State = s.State, LastPersistUtc = now };
            return;
        }

        var changed = sr.State != s.State;
        if (changed)
        {
            events.Add(new DeviceEvent
            {
                DeviceId = deviceId,
                TimestampUtc = now,
                Kind = DeviceEventKind.Sensor,
                Severity = s.State switch { SensorState.Critical => Severity.Major, SensorState.Warning => Severity.Warning, _ => Severity.Info },
                Message = $"{s.Kind} '{s.Name}' changed from {sr.State} to {s.State}",
            });
        }

        // Persist state changes immediately, values at most every 10 minutes.
        if (changed || now - sr.LastPersistUtc >= TimeSpan.FromMinutes(10))
        {
            await db.Sensors.Where(x => x.Id == sr.Id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.LastValue, s.Value)
                .SetProperty(x => x.LastState, s.State)
                .SetProperty(x => x.Name, s.Name)
                .SetProperty(x => x.LastSeenUtc, now), ct);
            sr.State = s.State;
            sr.LastPersistUtc = now;
        }
    }

    internal static string IfStatusText(int status) => status switch
    {
        1 => "up",
        2 => "down",
        3 => "testing",
        4 => "unknown",
        5 => "dormant",
        6 => "not present",
        7 => "lower layer down",
        _ => $"status {status}",
    };

    private static string FormatUptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{t.Hours}h {t.Minutes}m";
    }
}
