using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Processing;
using NetWatch.Core;
using NetWatch.Data.Config;
using NetWatch.Data.Events;

namespace NetWatch.Collectors.DomainControllers;

/// <summary>
/// Windows Event Forwarding: when enabled, reads DC Security events that a WEF subscription delivers to this
/// server's local "ForwardedEvents" log and stores them like remotely read events (matched to the DC device by
/// the event's computer name). Events keep their original record ID, so the (DC, record) key removes duplicates.
/// </summary>
public sealed class ForwardedEventsReader : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>On start, events of the last hour are re-read (duplicates are skipped) so a restart leaves no gap.</summary>
    private static readonly TimeSpan StartupLookback = TimeSpan.FromHours(1);

    private readonly SettingsService _settings;
    private readonly InventoryCache _inventory;
    private readonly DcResultProcessor _processor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NetWatch.Core.Alerts.IAlertSignalSink _alerts;
    private readonly ILogger<ForwardedEventsReader> _logger;
    private EventBookmark? _bookmark;
    private DateTime _since = DateTime.UtcNow - StartupLookback;

    public ForwardedEventsReader(SettingsService settings, InventoryCache inventory, DcResultProcessor processor, IServiceScopeFactory scopeFactory, NetWatch.Core.Alerts.IAlertSignalSink alerts, ILogger<ForwardedEventsReader> logger)
    {
        _settings = settings;
        _inventory = inventory;
        _processor = processor;
        _scopeFactory = scopeFactory;
        _alerts = alerts;
        _logger = logger;
    }

    /// <summary>Last read error (shown on the DC page), or null.</summary>
    public string? LastError { get; private set; }
    public DateTime? LastReadUtc { get; private set; }
    public long EventsRead { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            if (!_settings.Dc.AnyForwarding && !_settings.Endpoints.ReadForwardedEvents)
            {
                LastError = null;
                continue;
            }

            try
            {
                await ReadAsync(stoppingToken);
                LastError = null;
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
            {
                if (LastError is null)
                {
                    _logger.LogWarning(ex, "Reading the ForwardedEvents log failed");
                }

                LastError = ex is UnauthorizedAccessException
                    ? "Access denied: add the HIT SIEM service account (SYSTEM) to the local \"Event Log Readers\" group."
                    : ex.Message;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        var dcSettings = _settings.Dc;
        var endpointSettings = _settings.Endpoints;
        var readEndpoints = endpointSettings.ReadForwardedEvents;
        var max = readEndpoints ? Math.Max(dcSettings.MaxSecurityEventsPerPoll, endpointSettings.MaxForwardedPerCycle) : dcSettings.MaxSecurityEventsPerPoll;
        var batch = new Dictionary<int, List<DcSecurityReading>>();
        var endpointBatch = new Dictionary<int, List<Endpoints.EndpointEventReading>>();
        EventBookmark? newest = _bookmark;

        await Task.Run(() =>
        {
            var query = _bookmark is null
                ? new EventLogQuery(DcLogs.Forwarded, PathType.LogName,
                    string.Create(CultureInfo.InvariantCulture, $"*[System[TimeCreated[@SystemTime>='{_since:yyyy-MM-ddTHH:mm:ss.fffZ}']]]"))
                : new EventLogQuery(DcLogs.Forwarded, PathType.LogName, "*");
            using var reader = _bookmark is null ? new EventLogReader(query) : new EventLogReader(query, _bookmark);
            for (var i = 0; i < max; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                newest = record.Bookmark;
                if (record.LogName is not { } log)
                {
                    continue;
                }

                var source = FindDevice(record.MachineName);
                if (source is { Type: DeviceType.DomainController })
                {
                    // Only DCs set to WEF; the others are read remotely (and the unique index would drop duplicates anyway).
                    if (!log.Equals(DcLogs.Security, StringComparison.OrdinalIgnoreCase) || !dcSettings.UsesForwarding(source.Name, source.IpAddress) ||
                        DcEventParser.Parse(record.ToXml()) is not { } parsed)
                    {
                        continue;
                    }

                    if (!batch.TryGetValue(source.Id, out var list))
                    {
                        batch[source.Id] = list = new List<DcSecurityReading>();
                    }

                    list.Add(parsed);
                }
                else if (readEndpoints && source is not null && Endpoints.EndpointEventCatalog.Wants(log, record.Id) &&
                         Endpoints.EndpointEventCatalog.Parse(record.ToXml()) is { } endpointEvent)
                {
                    if (!endpointBatch.TryGetValue(source.Id, out var list))
                    {
                        endpointBatch[source.Id] = list = new List<Endpoints.EndpointEventReading>();
                    }

                    list.Add(endpointEvent);
                }
            }
        }, ct);

        _bookmark = newest;
        LastReadUtc = DateTime.UtcNow;
        if (batch.Count == 0 && endpointBatch.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        foreach (var (deviceId, readings) in batch)
        {
            if (_inventory.GetSnapshot(deviceId) is { } device)
            {
                EventsRead += await _processor.IngestSecurityAsync(device, readings, events, ct);
            }
        }

        foreach (var (deviceId, readings) in endpointBatch)
        {
            if (_inventory.GetSnapshot(deviceId) is { } device)
            {
                var stored = await Endpoints.EndpointEventCatalog.IngestAsync(device, readings, endpointSettings.StoreProcessStarts, events, _alerts, ct);
                EventsRead += stored;
                EndpointEventsRead += stored;
            }
        }
    }

    /// <summary>Endpoint (workstation / member server) events stored since start.</summary>
    public long EndpointEventsRead { get; private set; }

    /// <summary>Matches the event's computer name (FQDN or short name) to an inventory device.</summary>
    private DeviceSnapshot? FindDevice(string? computer)
    {
        if (string.IsNullOrWhiteSpace(computer))
        {
            return null;
        }

        return _inventory.FindByAddressOrName(null, computer) ?? _inventory.FindByAddressOrName(null, computer.Split('.')[0]);
    }
}
