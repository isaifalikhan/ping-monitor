using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Processing;
using NetWatch.Data.Config;

namespace NetWatch.Collectors.Scheduling;

/// <summary>
/// Schedules device polls. Each device is polled every <c>PollIntervalSec</c> with an initial random offset
/// (jitter) so load is spread evenly. A fixed pool of workers bounds concurrency; a device is never polled
/// twice at the same time.
/// </summary>
public sealed class PollScheduler : BackgroundService
{
    private readonly InventoryCache _inventory;
    private readonly IDevicePoller _poller;
    private readonly PollResultProcessor _processor;
    private readonly SettingsService _settings;
    private readonly CollectorStats _stats;
    private readonly TimeProvider _time;
    private readonly ILogger<PollScheduler> _logger;
    private readonly ConcurrentDictionary<int, DateTime> _nextDue = new();
    /// <summary>Devices queued or being polled: when queued, and when a worker started (0 = still queued).</summary>
    private readonly ConcurrentDictionary<int, (long QueuedTs, long StartedTs)> _inFlight = new();
    private readonly Channel<DeviceSnapshot> _queue = Channel.CreateUnbounded<DeviceSnapshot>(new UnboundedChannelOptions { SingleWriter = true });

    public PollScheduler(
        InventoryCache inventory,
        IDevicePoller poller,
        PollResultProcessor processor,
        SettingsService settings,
        CollectorStats stats,
        TimeProvider time,
        ILogger<PollScheduler> logger)
    {
        _inventory = inventory;
        _poller = poller;
        _processor = processor;
        _settings = settings;
        _stats = stats;
        _time = time;
        _logger = logger;
    }

    /// <summary>Requests an immediate poll of a device (e.g. after it was added or edited).</summary>
    public void PollNow(int deviceId) => _nextDue[deviceId] = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _inventory.Ready.WaitAsync(stoppingToken);
        await _processor.InitialiseAsync(stoppingToken);

        var workerCount = _settings.Monitoring.MaxConcurrentPolls;
        _logger.LogInformation("Poll scheduler started with {Workers} workers for {Devices} devices", workerCount, _inventory.All.Count);
        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() => WorkerAsync(stoppingToken), CancellationToken.None)).ToList();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                ScheduleDue();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _queue.Writer.TryComplete();
            await Task.WhenAll(workers);
        }
    }

    private void ScheduleDue()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var known = new HashSet<int>();
        foreach (var device in _inventory.All)
        {
            known.Add(device.Id);
            if (!device.IsEnabled)
            {
                continue;
            }

            if (!_nextDue.TryGetValue(device.Id, out var due))
            {
                // First sighting: spread devices across one interval.
                due = now.AddMilliseconds(Random.Shared.NextDouble() * Math.Min(device.PollIntervalSec, 60) * 1000);
                _nextDue[device.Id] = due;
            }

            if (now < due)
            {
                continue;
            }

            if (_inFlight.TryGetValue(device.Id, out var busy))
            {
                _stats.Overrun();
                var queuedFor = _time.GetElapsedTime(busy.QueuedTs);
                var running = busy.StartedTs == 0 ? TimeSpan.Zero : _time.GetElapsedTime(busy.StartedTs);
                _logger.LogWarning(
                    "Poll overrun: {Device} ({Ip}) is due again but its previous poll is still {Stage} ({QueuedSec:0.0} s since queued, {RunningSec:0.0} s running, interval {Interval} s); this cycle is skipped",
                    device.Name, device.IpAddress, busy.StartedTs == 0 ? "waiting for a worker" : "running", queuedFor.TotalSeconds, running.TotalSeconds, device.PollIntervalSec);
                _nextDue[device.Id] = now.AddSeconds(device.PollIntervalSec);
                continue;
            }

            // Keep a steady cadence; if we fell behind (e.g. after a pause) restart the cadence from now.
            var next = (due == DateTime.MinValue ? now : due).AddSeconds(device.PollIntervalSec);
            if (next < now)
            {
                next = now.AddSeconds(device.PollIntervalSec);
            }

            _nextDue[device.Id] = next;
            _inFlight[device.Id] = (_time.GetTimestamp(), 0);
            _stats.Queue();
            _queue.Writer.TryWrite(device);
        }

        // Forget deleted devices.
        foreach (var id in _nextDue.Keys.Where(id => !known.Contains(id)).ToList())
        {
            _nextDue.TryRemove(id, out _);
            _processor.Forget(id);
        }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        await foreach (var device in _queue.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (ct.IsCancellationRequested)
            {
                _inFlight.TryRemove(device.Id, out _);
                continue;
            }

            _stats.Start();
            var started = _time.GetTimestamp();
            if (_inFlight.TryGetValue(device.Id, out var queued))
            {
                _inFlight[device.Id] = (queued.QueuedTs, started);
            }

            var failed = false;
            try
            {
                // A poll may not take longer than its interval (minus a margin), or it would overlap the next one.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(device.PollIntervalSec - 5, 10, 120)));

                var request = await _processor.PrepareAsync(device, timeout.Token);
                var result = await _poller.PollAsync(request, timeout.Token);
                result.Duration = _time.GetElapsedTime(started);
                failed = !result.PingOk && result.SnmpOk != true && result.Server?.Ok != true;
                await _processor.ProcessAsync(request, result, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                failed = true;
                _logger.LogWarning(ex, "Poll of {Device} ({Ip}) failed", device.Name, device.IpAddress);
            }
            finally
            {
                _stats.Complete(_time.GetElapsedTime(started), failed);
                _inFlight.TryRemove(device.Id, out _);
            }
        }
    }
}

/// <summary>Persists interface counter state every 5 minutes and on shutdown.</summary>
public sealed class CounterStatePersister : BackgroundService
{
    private readonly PollResultProcessor _processor;
    private readonly Core.Abstractions.IMetricStore _store;
    private readonly ILogger<CounterStatePersister> _logger;

    public CounterStatePersister(PollResultProcessor processor, Core.Abstractions.IMetricStore store, ILogger<CounterStatePersister> logger)
    {
        _processor = processor;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SaveAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await SaveAsync(CancellationToken.None);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            var states = _processor.GetCounterStates();
            if (states.Count > 0)
            {
                await _store.SaveCounterStatesAsync(states, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not persist interface counter state");
        }
    }
}
