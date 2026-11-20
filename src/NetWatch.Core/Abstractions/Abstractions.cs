using NetWatch.Core.Metrics;

namespace NetWatch.Core.Abstractions;

/// <summary>
/// Time-series storage. Writes are queued and flushed in batches by a single writer; reads choose the
/// resolution (raw / 5-minute / hourly) from the requested range.
/// </summary>
public interface IMetricStore
{
    /// <summary>Queues a sample. Never blocks; if the queue is full the oldest samples are dropped and counted.</summary>
    void Enqueue(in InterfaceSample sample);

    /// <summary>Queues a sample. Never blocks.</summary>
    void Enqueue(in DeviceSample sample);

    Task<SeriesData> QueryInterfaceAsync(int interfaceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    Task<SeriesData> QueryDeviceAsync(int deviceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    Task<IReadOnlyDictionary<int, CounterSnapshot>> LoadCounterStatesAsync(CancellationToken ct);

    Task SaveCounterStatesAsync(IReadOnlyCollection<KeyValuePair<int, CounterSnapshot>> states, CancellationToken ct);

    /// <summary>Samples waiting to be written (self-monitoring).</summary>
    int PendingWrites { get; }

    /// <summary>Samples dropped because the queue overflowed since start (self-monitoring).</summary>
    long DroppedSamples { get; }
}

/// <summary>Encrypts secrets at rest (Windows DPAPI, machine scope).</summary>
public interface ISecretProtector
{
    byte[] Protect(string plaintext);

    string Unprotect(byte[] protectedData);
}

/// <summary>Pushes live updates to connected browsers (implemented with SignalR).</summary>
public interface ILiveUpdatePublisher
{
    /// <param name="eventName">Client-side event name, e.g. "alertRaised".</param>
    /// <param name="payload">Serialised as JSON.</param>
    /// <param name="group">Optional SignalR group (e.g. "device:12"); null = all clients.</param>
    Task PublishAsync(string eventName, object payload, string? group = null);
}

/// <summary>Raised by the API when inventory or rules change so caches reload promptly.</summary>
public interface IInventoryChangeNotifier
{
    void NotifyInventoryChanged();
}
