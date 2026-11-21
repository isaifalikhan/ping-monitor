using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Hardware;
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

namespace NetWatch.Collectors.Processing;

/// <summary>
/// Server hardware health (iLO / iDRAC over Redfish, or the HPE/Dell SNMP agent) and GPUs: stores the latest
/// components and GPUs, publishes metrics and states to the alert engine, pins the BMC certificate on first use.
/// </summary>
public sealed class HardwareResultProcessor
{
    private readonly IMetricStore _metrics;
    private readonly IAlertSignalSink _alerts;
    private readonly SettingsService _settings;
    private readonly CredentialResolver _credentials;
    private readonly TimeProvider _time;
    private readonly ILogger<HardwareResultProcessor> _logger;
    private readonly ConcurrentDictionary<int, DateTime> _lastRead = new();

    public HardwareResultProcessor(IMetricStore metrics, IAlertSignalSink alerts, SettingsService settings, CredentialResolver credentials, TimeProvider time,
        ILogger<HardwareResultProcessor> logger)
    {
        _metrics = metrics;
        _alerts = alerts;
        _settings = settings;
        _credentials = credentials;
        _time = time;
        _logger = logger;
    }

    public void Forget(int deviceId) => _lastRead.TryRemove(deviceId, out _);

    /// <summary>A hardware read every <see cref="HardwareSettings.PollIntervalMin"/> for devices with a BMC address.</summary>
    public async Task<HardwarePollRequest?> PrepareAsync(DeviceSnapshot device, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(device.BmcAddress))
        {
            return null;
        }

        var s = _settings.Hardware;
        var now = _time.GetUtcNow().UtcDateTime;
        if (_lastRead.TryGetValue(device.Id, out var last) && now - last < TimeSpan.FromMinutes(s.PollIntervalMin))
        {
            return null;
        }

        _lastRead[device.Id] = now;
        var redfish = device.BmcCredentialId is { } id ? await _credentials.GetRedfishAsync(id, ct) : null;
        var snmp = redfish is null && device.SnmpCredentialId is { } sid ? await _credentials.GetSnmpAsync(sid, ct) : null;
        return redfish is null && snmp is null
            ? null
            : new HardwarePollRequest(device.BmcAddress!, redfish, snmp, device.BmcTlsThumbprint, s.TrustOnFirstUse, TimeSpan.FromSeconds(s.TimeoutSec));
    }

    public async Task ProcessHardwareAsync(DeviceSnapshot device, HardwarePollData data, NetWatchDbContext db, DateTime now, CancellationToken ct)
    {
        _alerts.Publish(new StateSignal(device.Id, null, StateKeys.HardwareUnreachable, null, !data.Ok,
            data.Ok ? $"{data.Source} answers" : $"{data.Source} on {device.BmcAddress}: {data.Error}", now));
        if (data.NewTlsThumbprint is { } pin)
        {
            await db.Devices.Where(d => d.Id == device.Id && d.BmcTlsThumbprint == null).ExecuteUpdateAsync(u => u.SetProperty(d => d.BmcTlsThumbprint, pin), ct);
            _logger.LogInformation("Pinned the management controller certificate of {Device} (SHA-256 {Thumbprint})", device.Name, pin);
        }

        if (!data.Ok)
        {
            return;
        }

        var existing = await db.HardwareComponents.Where(c => c.DeviceId == device.Id).ToListAsync(ct);
        var seen = new HashSet<int>();
        foreach (var r in data.Components.GroupBy(c => (c.Category, c.Name)).Select(g => g.First()))
        {
            var row = existing.FirstOrDefault(c => c.Category == r.Category && c.Name == Clip(r.Name, 255));
            if (row is null)
            {
                row = new HardwareComponent { DeviceId = device.Id, Category = r.Category, Name = Clip(r.Name, 255)! };
                db.HardwareComponents.Add(row);
            }
            else
            {
                seen.Add(row.Id);
            }

            row.Source = data.Source;
            row.Health = Clip(r.Health, 16);
            row.State = Clip(r.State, 32);
            row.Reading = r.Reading;
            row.Unit = Clip(r.Unit, 16);
            row.Details = Clip(r.Details, 1000);
            row.LastSeenUtc = now;

            var instance = $"{r.Category}:{row.Name}";
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.HardwareComponent, instance, r.IsProblem,
                r.IsProblem ? $"{Label(r.Category)} '{r.Name}' reports {r.Health}{(r.Details is null ? string.Empty : $" ({r.Details})")}" : $"{Label(r.Category)} '{r.Name}' is OK",
                now, r.Name));
        }

        // Components that disappeared from the controller's report: clear their alerts and remove them.
        foreach (var gone in existing.Where(c => !seen.Contains(c.Id)))
        {
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.HardwareComponent, $"{gone.Category}:{gone.Name}", false, "Component no longer reported", now, gone.Name));
            db.HardwareComponents.Remove(gone);
        }

        await db.SaveChangesAsync(ct);
        if (data.PowerWatts is { } watts)
        {
            var id = _metrics.GetOrCreateSeries(device.Id, MetricKeys.ServerPower, null);
            _metrics.Enqueue(new GenericSample(id, new DateTimeOffset(now).ToUnixTimeMilliseconds(), watts));
        }
    }

    /// <summary>Stores the GPUs of a server poll; a GPU seen before but missing now is a problem.</summary>
    public async Task ProcessGpusAsync(DeviceSnapshot device, ServerPollData server, NetWatchDbContext db, DateTime now, CancellationToken ct)
    {
        if (!server.Ok || !server.GpusRead)
        {
            return;
        }

        var tsMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        var existing = await db.ServerGpus.Where(g => g.DeviceId == device.Id).ToListAsync(ct);
        foreach (var g in server.Gpus)
        {
            var row = existing.FirstOrDefault(x => x.Index == g.Index);
            if (row is null)
            {
                row = new ServerGpu { DeviceId = device.Id, Index = g.Index, FirstSeenUtc = now };
                db.ServerGpus.Add(row);
                existing.Add(row);
            }

            row.Name = Clip(g.Name, 255)!;
            row.Vendor = Clip(g.Vendor, 64);
            row.DriverVersion = Clip(g.DriverVersion, 64);
            row.UtilizationPct = g.UtilizationPct;
            row.MemoryUsedMb = g.MemoryUsedMb;
            row.MemoryTotalMb = g.MemoryTotalMb;
            row.TemperatureC = g.TemperatureC;
            row.PowerW = g.PowerW;
            row.FanPct = g.FanPct;
            row.Problem = Clip(g.Problem, 500);
            row.LastSeenUtc = now;

            var instance = g.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var label = $"GPU {g.Index} ({g.Name})";
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.GpuProblem, instance, g.Problem is not null,
                g.Problem is null ? $"{label} is OK" : $"{label}: {g.Problem}", now, label));
            Metric(MetricKeys.GpuUtilization, instance, label, g.UtilizationPct);
            Metric(MetricKeys.GpuMemory, instance, label, g.MemoryPct);
            Metric(MetricKeys.GpuTemperature, instance, label, g.TemperatureC);
            Metric(MetricKeys.GpuPower, instance, label, g.PowerW);
        }

        foreach (var missing in existing.Where(x => x.LastSeenUtc < now))
        {
            var label = $"GPU {missing.Index} ({missing.Name})";
            missing.Problem = "not detected any more (removed, failed or driver not loaded)";
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.GpuProblem, missing.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), true,
                $"{label} is no longer detected", now, label));
        }

        await db.SaveChangesAsync(ct);

        void Metric(string key, string instance, string label, double? value)
        {
            if (value is not { } v)
            {
                return;
            }

            _alerts.Publish(new MetricSignal(device.Id, null, key, instance, v, now, label));
            _metrics.Enqueue(new GenericSample(_metrics.GetOrCreateSeries(device.Id, key, instance), tsMs, v));
        }
    }

    private static string Label(string category) => category switch
    {
        "PowerSupply" => "Power supply",
        "StorageController" => "Storage controller",
        _ => category,
    };

    private static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
