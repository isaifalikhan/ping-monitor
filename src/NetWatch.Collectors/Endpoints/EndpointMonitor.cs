using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Config;

namespace NetWatch.Collectors.Endpoints;

/// <summary>
/// Every 5 minutes: raises "Endpoint stopped reporting" for machines silent longer than the configured hours,
/// marks such workstations Unknown, and purges old inventory changes.
/// </summary>
public sealed class EndpointMonitor : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly EndpointStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertSignalSink _alerts;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<EndpointMonitor> _logger;
    private DateTime _lastPurge = DateTime.MinValue;

    public EndpointMonitor(EndpointStore store, IServiceScopeFactory scopeFactory, IAlertSignalSink alerts, SettingsService settings, TimeProvider time, ILogger<EndpointMonitor> logger)
    {
        _store = store;
        _scopeFactory = scopeFactory;
        _alerts = alerts;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), _time, stoppingToken);
            using var timer = new PeriodicTimer(Interval, _time);
            do
            {
                try
                {
                    await CheckAsync(_time.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Endpoint stale check failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One check (internal for tests). Returns the number of stale machines.</summary>
    internal async Task<int> CheckAsync(DateTime now, CancellationToken ct)
    {
        var settings = _settings.Endpoints;
        var limit = TimeSpan.FromHours(settings.StaleHours);
        var stale = _store.All().Where(e => e.DeviceId is not null && now - e.LastSeenUtc > limit).ToList();
        foreach (var e in stale)
        {
            var hours = (now - e.LastSeenUtc).TotalHours;
            _alerts.Publish(new StateSignal(e.DeviceId!.Value, null, StateKeys.EndpointStale, null, true,
                string.Create(CultureInfo.InvariantCulture, $"No inventory from {e.Name} for {hours:0} h (last at {e.LastSeenUtc:yyyy-MM-dd HH:mm} UTC)"), now));
        }

        if (stale.Count > 0)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var ids = stale.Select(e => e.DeviceId!.Value).ToList();
            await db.Devices.Where(d => ids.Contains(d.Id) && d.Type == DeviceType.Workstation && d.Status == DeviceStatus.Up)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeviceStatus.Unknown).SetProperty(d => d.StatusSinceUtc, now), ct);
        }

        if (now - _lastPurge > TimeSpan.FromHours(1))
        {
            _lastPurge = now;
            await _store.PurgeChangesAsync(now.AddDays(-settings.ChangeRetentionDays));
        }

        return stale.Count;
    }
}
