using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Data.Events;

namespace NetWatch.Collectors.Receivers;

/// <summary>Counters for the syslog/trap receivers (self-monitoring page).</summary>
public sealed class ReceiverStats
{
    private long _syslogReceived;
    private long _trapsReceived;
    private long _stored;
    private long _dropped;
    private long _unknownSource;
    private long _parseErrors;

    public long SyslogReceived => Interlocked.Read(ref _syslogReceived);
    public long TrapsReceived => Interlocked.Read(ref _trapsReceived);
    public long Stored => Interlocked.Read(ref _stored);

    /// <summary>Dropped by the rate limit, a full queue, or because the source is unknown and unknown sources are rejected.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);
    public long UnknownSource => Interlocked.Read(ref _unknownSource);
    public long ParseErrors => Interlocked.Read(ref _parseErrors);

    /// <summary>Listener state per endpoint ("syslog/udp:514" → "listening" or the bind error).</summary>
    public Dictionary<string, string> Listeners { get; } = new(StringComparer.Ordinal);

    internal void Syslog() => Interlocked.Increment(ref _syslogReceived);
    internal void Trap() => Interlocked.Increment(ref _trapsReceived);
    internal void StoredMany(int n) => Interlocked.Add(ref _stored, n);
    internal void Drop() => Interlocked.Increment(ref _dropped);
    internal void Unknown() => Interlocked.Increment(ref _unknownSource);
    internal void ParseError() => Interlocked.Increment(ref _parseErrors);

    internal void SetListener(string name, string state)
    {
        lock (Listeners)
        {
            Listeners[name] = state;
        }
    }

    public IReadOnlyDictionary<string, string> ListenerSnapshot()
    {
        lock (Listeners)
        {
            return new Dictionary<string, string>(Listeners);
        }
    }
}

/// <summary>
/// Single writer for high-volume received events (syslog, traps) into events.db. Receivers enqueue without
/// blocking; batches are written once a second in one transaction. A global rate limit protects the database
/// from floods (excess messages are counted and dropped).
/// </summary>
public sealed class EventIngestService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SettingsService _settings;
    private readonly ReceiverStats _stats;
    private readonly ILogger<EventIngestService> _logger;
    private readonly Channel<object> _queue;
    private long _windowSecond;
    private int _windowCount;
    private readonly object _rateLock = new();

    public EventIngestService(IServiceScopeFactory scopeFactory, SettingsService settings, ReceiverStats stats, ILogger<EventIngestService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _stats = stats;
        _logger = logger;
        _queue = Channel.CreateBounded<object>(
            new BoundedChannelOptions(100_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => stats.Drop());
    }

    public int Queued => _queue.Reader.CanCount ? _queue.Reader.Count : -1;

    /// <summary>Applies the per-second rate limit. Returns false when the message must be dropped.</summary>
    public bool Admit()
    {
        var second = Environment.TickCount64 / 1000;
        lock (_rateLock)
        {
            if (second != _windowSecond)
            {
                _windowSecond = second;
                _windowCount = 0;
            }

            if (++_windowCount > _settings.Receivers.MaxMessagesPerSecond)
            {
                _stats.Drop();
                return false;
            }

            return true;
        }
    }

    public void Enqueue(SyslogMessage message) => _queue.Writer.TryWrite(message);

    public void Enqueue(SnmpTrap trap) => _queue.Writer.TryWrite(trap);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<object>(5000);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _queue.Reader.WaitToReadAsync(stoppingToken))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Final flush below.
            }

            batch.Clear();
            while (batch.Count < 20_000 && _queue.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count > 0)
            {
                await WriteAsync(batch);
            }
        }
    }

    private async Task WriteAsync(List<object> batch)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.SyslogMessages.AddRange(batch.OfType<SyslogMessage>());
            db.SnmpTraps.AddRange(batch.OfType<SnmpTrap>());
            await db.SaveChangesAsync(CancellationToken.None);
            _stats.StoredMany(batch.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store {Count} received syslog/trap message(s)", batch.Count);
        }
    }
}
