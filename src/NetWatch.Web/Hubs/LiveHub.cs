using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Live;
using NetWatch.Collectors.Processing;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Web.Api;
using NetWatch.Web.Auth;

namespace NetWatch.Web.Hubs;

/// <summary>
/// Live updates to browsers. All authenticated users receive alert and status events; device detail pages
/// join a per-device group to receive per-poll metrics.
/// </summary>
[Authorize(Policy = Policies.Viewer)]
public sealed class LiveHub : Hub
{
    public Task JoinDevice(int deviceId) => Groups.AddToGroupAsync(Context.ConnectionId, LiveEvents.DeviceGroup(deviceId));

    public Task LeaveDevice(int deviceId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveEvents.DeviceGroup(deviceId));
}

/// <summary>SignalR implementation of <see cref="ILiveUpdatePublisher"/>. Never throws into collectors.</summary>
public sealed class SignalRLivePublisher : ILiveUpdatePublisher
{
    private readonly IHubContext<LiveHub> _hub;
    private readonly ILogger<SignalRLivePublisher> _logger;

    public SignalRLivePublisher(IHubContext<LiveHub> hub, ILogger<SignalRLivePublisher> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task PublishAsync(string eventName, object payload, string? group = null)
    {
        try
        {
            var clients = group is null ? _hub.Clients.All : _hub.Clients.Group(group);
            await clients.SendAsync(eventName, payload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish live event {Event}", eventName);
        }
    }
}

/// <summary>Broadcasts a small status/alert summary every 10 seconds (keeps NOC screens and badges current).</summary>
public sealed class SummaryBroadcaster : BackgroundService
{
    private readonly InventoryCache _inventory;
    private readonly LiveStateCache _live;
    private readonly IActiveAlertIndex _alerts;
    private readonly ILiveUpdatePublisher _publisher;

    public SummaryBroadcaster(InventoryCache inventory, LiveStateCache live, IActiveAlertIndex alerts, ILiveUpdatePublisher publisher)
    {
        _inventory = inventory;
        _live = live;
        _alerts = alerts;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var statuses = _inventory.All.Where(d => d.IsEnabled).Select(d =>
                    DeviceStatusHelper.Effective(_live.GetDevice(d.Id)?.Status ?? DeviceStatus.Unknown, true, d.InMaintenance, _alerts.GetWorstSeverity(d.Id)));
                var counts = _alerts.CountBySeverity();
                await _publisher.PublishAsync(LiveEvents.Summary, new
                {
                    devices = OverviewEndpoints.Count(statuses),
                    alerts = Enum.GetValues<Severity>().ToDictionary(s => s.ToString(), s => counts.GetValueOrDefault(s)),
                    serverTimeUtc = DateTime.UtcNow,
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
