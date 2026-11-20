namespace NetWatch.Collectors.Scheduling;

/// <summary>Self-monitoring counters for the poll engine.</summary>
public sealed class CollectorStats
{
    private long _totalPolls;
    private long _failedPolls;
    private long _overruns;
    private int _inFlight;
    private int _queued;
    private double _avgDurationMs;
    private double _maxDurationMs;
    private readonly object _lock = new();

    public long TotalPolls => Interlocked.Read(ref _totalPolls);

    /// <summary>Polls where the device was unreachable or the poll threw.</summary>
    public long FailedPolls => Interlocked.Read(ref _failedPolls);

    /// <summary>Times a device was due again while its previous poll was still running.</summary>
    public long Overruns => Interlocked.Read(ref _overruns);
    public int InFlight => Volatile.Read(ref _inFlight);
    public int Queued => Volatile.Read(ref _queued);

    public double AvgDurationMs
    {
        get
        {
            lock (_lock)
            {
                return _avgDurationMs;
            }
        }
    }

    public double MaxDurationMs
    {
        get
        {
            lock (_lock)
            {
                return _maxDurationMs;
            }
        }
    }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    internal void Queue() => Interlocked.Increment(ref _queued);

    internal void Start()
    {
        Interlocked.Decrement(ref _queued);
        Interlocked.Increment(ref _inFlight);
    }

    internal void Overrun() => Interlocked.Increment(ref _overruns);

    internal void Complete(TimeSpan duration, bool failed)
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _totalPolls);
        if (failed)
        {
            Interlocked.Increment(ref _failedPolls);
        }

        lock (_lock)
        {
            // Exponential moving average over roughly the last 200 polls.
            var ms = duration.TotalMilliseconds;
            _avgDurationMs = _avgDurationMs == 0 ? ms : (_avgDurationMs * 0.995) + (ms * 0.005);
            _maxDurationMs = Math.Max(_maxDurationMs * 0.999, ms);
        }
    }
}
