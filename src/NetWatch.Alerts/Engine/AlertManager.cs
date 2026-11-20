using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;

namespace NetWatch.Alerts.Engine;

/// <summary>
/// Hosts the alert engine: consumes signals from collectors, evaluates rules, persists alert transitions and
/// pushes them to browsers. Signals are processed by a single loop, so evaluator state needs no locking
/// except for manual resets coming from the API.
/// </summary>
public sealed class AlertManager : BackgroundService, IAlertSignalSink, IActiveAlertIndex
{
    private const int BatchSize = 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceContextProvider _devices;
    private readonly ILiveUpdatePublisher _live;
    private readonly ILogger<AlertManager> _logger;
    private readonly Channel<AlertSignal> _signals;
    private readonly AlertEvaluator _evaluator = new();
    private readonly object _evaluatorLock = new();

    /// <summary>Open (Active/Acknowledged) alerts by dedup key.</summary>
    private readonly ConcurrentDictionary<string, OpenAlert> _open = new(StringComparer.Ordinal);

    private volatile IReadOnlyList<AlertRuleDefinition> _rules = Array.Empty<AlertRuleDefinition>();
    private volatile bool _rulesDirty = true;
    private volatile IReadOnlyDictionary<int, Severity> _worstByDevice = new Dictionary<int, Severity>();
    private long _droppedSignals;
    private long _processedSignals;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record OpenAlert(long Id, int? DeviceId, Severity Severity, AlertState State);

    public AlertManager(IServiceScopeFactory scopeFactory, IDeviceContextProvider devices, ILiveUpdatePublisher live, ILogger<AlertManager> logger)
    {
        _scopeFactory = scopeFactory;
        _devices = devices;
        _live = live;
        _logger = logger;
        _signals = Channel.CreateBounded<AlertSignal>(
            new BoundedChannelOptions(200_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _droppedSignals));
    }

    public long DroppedSignals => Interlocked.Read(ref _droppedSignals);

    public long ProcessedSignals => Interlocked.Read(ref _processedSignals);

    public int QueuedSignals => _signals.Reader.CanCount ? _signals.Reader.Count : -1;

    /// <summary>Completes once open alerts and rules are loaded.</summary>
    public Task Ready => _ready.Task;

    public void Publish(AlertSignal signal) => _signals.Writer.TryWrite(signal);

    /// <summary>Called by the API after a rule is created, edited or deleted.</summary>
    public void InvalidateRules() => _rulesDirty = true;

    public Severity? GetWorstSeverity(int deviceId) =>
        _worstByDevice.TryGetValue(deviceId, out var s) ? s : null;

    public IReadOnlyDictionary<Severity, int> CountBySeverity() =>
        _open.Values.GroupBy(a => a.Severity).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Updates the in-memory state after an operator acknowledged an alert.</summary>
    internal void OnAcknowledged(string dedupKey, long id, int? deviceId, Severity severity)
    {
        _open[dedupKey] = new OpenAlert(id, deviceId, severity, AlertState.Acknowledged);
    }

    /// <summary>Updates the in-memory state after an operator closed an open alert, so the condition can raise again.</summary>
    internal void OnClosed(string dedupKey)
    {
        _open.TryRemove(dedupKey, out _);
        lock (_evaluatorLock)
        {
            _evaluator.Reset(dedupKey);
        }

        RebuildDeviceIndex();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await LoadOpenAlertsAsync(stoppingToken);
            await ReloadRulesAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Alert engine failed to initialise; it will retry rule loading on the next batch");
        }
        finally
        {
            _ready.TrySetResult();
        }

        var batch = new List<AlertSignal>(BatchSize);
        var reader = _signals.Reader;
        while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < BatchSize && reader.TryRead(out var signal))
            {
                batch.Add(signal);
            }

            try
            {
                if (_rulesDirty)
                {
                    await ReloadRulesAsync(stoppingToken);
                }

                await ProcessBatchAsync(batch, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to process {Count} alert signal(s)", batch.Count);
            }
        }
    }

    private async Task ProcessBatchAsync(List<AlertSignal> batch, CancellationToken ct)
    {
        var rules = _rules;
        var transitions = new List<AlertTransition>();
        lock (_evaluatorLock)
        {
            foreach (var signal in batch)
            {
                var device = _devices.Get(signal.DeviceId);
                if (device is null)
                {
                    continue;
                }

                transitions.AddRange(_evaluator.Evaluate(signal, device, rules));
            }
        }

        Interlocked.Add(ref _processedSignals, batch.Count);
        if (transitions.Count > 0)
        {
            await PersistAsync(transitions, ct);
        }
    }

    private async Task PersistAsync(IReadOnlyList<AlertTransition> transitions, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var notifications = new List<(string Event, Alert Alert)>();

        foreach (var t in transitions)
        {
            if (t.Kind == TransitionKind.Raise)
            {
                var alert = await RaiseAsync(db, t, ct);
                if (alert is not null)
                {
                    notifications.Add((alert.OccurrenceCount == 1 ? AlertEvents.Raised : AlertEvents.Updated, alert));
                }
            }
            else
            {
                var alert = await ResolveAsync(db, t, ct);
                if (alert is not null)
                {
                    notifications.Add((AlertEvents.Resolved, alert));
                }
            }
        }

        RebuildDeviceIndex();
        foreach (var (evt, alert) in notifications)
        {
            var name = alert.DeviceId is { } id ? _devices.Get(id)?.Name : null;
            await _live.PublishAsync(evt, AlertDto.From(alert, name));
        }
    }

    private async Task<Alert?> RaiseAsync(NetWatchDbContext db, AlertTransition t, CancellationToken ct)
    {
        if (_open.TryGetValue(t.DedupKey, out var open))
        {
            // Already open (e.g. repeated event): refresh it instead of creating a duplicate.
            var existing = await db.Alerts.FirstOrDefaultAsync(a => a.Id == open.Id, ct);
            if (existing is null || existing.State is AlertState.Resolved or AlertState.Closed)
            {
                _open.TryRemove(t.DedupKey, out _);
            }
            else
            {
                existing.LastSeenUtc = t.TimestampUtc;
                existing.OccurrenceCount++;
                existing.CurrentValue = t.Value;
                existing.Message = t.Message;
                existing.Severity = t.Rule.Severity;
                await db.SaveChangesAsync(ct);
                _open[t.DedupKey] = open with { Severity = existing.Severity };
                return existing;
            }
        }

        var alert = new Alert
        {
            RuleId = t.Rule.Id,
            DeviceId = t.DeviceId,
            InterfaceId = t.InterfaceId,
            DedupKey = t.DedupKey,
            Severity = t.Rule.Severity,
            State = AlertState.Active,
            Title = t.Title,
            Message = t.Message,
            CurrentValue = t.Value,
            AutoResolve = t.AutoResolve,
            FirstSeenUtc = t.TimestampUtc,
            LastSeenUtc = t.TimestampUtc,
            OccurrenceCount = 1,
        };
        alert.History.Add(new AlertHistoryEntry { TimestampUtc = t.TimestampUtc, Action = "Raised", Comment = t.Message });
        db.Alerts.Add(alert);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Unique open-alert index violated: another writer opened it meanwhile. Log and skip.
            _logger.LogWarning(ex, "Open alert already exists for {DedupKey}", t.DedupKey);
            db.ChangeTracker.Clear();
            return null;
        }

        _open[t.DedupKey] = new OpenAlert(alert.Id, alert.DeviceId, alert.Severity, AlertState.Active);
        _logger.LogInformation("Alert raised [{Severity}] {Title}: {Message}", alert.Severity, alert.Title, alert.Message);
        return alert;
    }

    private async Task<Alert?> ResolveAsync(NetWatchDbContext db, AlertTransition t, CancellationToken ct)
    {
        if (!_open.TryRemove(t.DedupKey, out var open))
        {
            return null;
        }

        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == open.Id, ct);
        if (alert is null || alert.State is AlertState.Resolved or AlertState.Closed)
        {
            return null;
        }

        alert.State = AlertState.Resolved;
        alert.ResolvedUtc = t.TimestampUtc;
        alert.LastSeenUtc = t.TimestampUtc;
        alert.CurrentValue = t.Value ?? alert.CurrentValue;
        db.AlertHistory.Add(new AlertHistoryEntry { AlertId = alert.Id, TimestampUtc = t.TimestampUtc, Action = "Resolved", Comment = t.Message });
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Alert resolved {Title}: {Message}", alert.Title, t.Message);
        return alert;
    }

    private async Task LoadOpenAlertsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var open = await db.Alerts.AsNoTracking()
            .Where(a => a.State == AlertState.Active || a.State == AlertState.Acknowledged)
            .Select(a => new { a.Id, a.DedupKey, a.DeviceId, a.Severity, a.State, a.AutoResolve })
            .ToListAsync(ct);

        lock (_evaluatorLock)
        {
            foreach (var a in open)
            {
                _open[a.DedupKey] = new OpenAlert(a.Id, a.DeviceId, a.Severity, a.State);
                if (a.AutoResolve)
                {
                    // Lets a recovered condition resolve an alert that was raised before a restart.
                    _evaluator.MarkRaised(a.DedupKey);
                }
            }
        }

        RebuildDeviceIndex();
        _logger.LogInformation("Alert engine loaded {Count} open alert(s)", open.Count);
    }

    private async Task ReloadRulesAsync(CancellationToken ct)
    {
        _rulesDirty = false;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var entities = await db.AlertRules.AsNoTracking().ToListAsync(ct);
        var enabled = entities.Where(r => r.IsEnabled).Select(AlertRuleDefinition.From).ToList();
        var previous = _rules.ToDictionary(r => r.Id);
        _rules = enabled;

        // Rules that were edited lose their timing state; open alerts of disabled/deleted rules are resolved.
        var enabledIds = enabled.Select(r => r.Id).ToHashSet();
        lock (_evaluatorLock)
        {
            foreach (var rule in enabled)
            {
                if (previous.TryGetValue(rule.Id, out var old) && old != rule && !SameCondition(old, rule))
                {
                    _evaluator.ResetRule(rule.Id);
                    foreach (var key in _open.Keys.Where(k => AlertEvaluator.TryParseRuleId(k, out var id) && id == rule.Id))
                    {
                        _evaluator.MarkRaised(key);
                    }
                }
            }
        }

        var orphaned = _open
            .Where(kv => AlertEvaluator.TryParseRuleId(kv.Key, out var id) && !enabledIds.Contains(id))
            .ToList();
        foreach (var (key, open) in orphaned)
        {
            _open.TryRemove(key, out _);
            var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == open.Id, ct);
            if (alert is null || alert.State is AlertState.Resolved or AlertState.Closed)
            {
                continue;
            }

            alert.State = AlertState.Resolved;
            alert.ResolvedUtc = DateTime.UtcNow;
            db.AlertHistory.Add(new AlertHistoryEntry { AlertId = alert.Id, TimestampUtc = DateTime.UtcNow, Action = "Resolved", Comment = "Rule disabled or deleted" });
            await db.SaveChangesAsync(ct);
            await _live.PublishAsync(AlertEvents.Resolved, AlertDto.From(alert, alert.DeviceId is { } d ? _devices.Get(d)?.Name : null));
        }

        foreach (var id in previous.Keys.Where(id => !enabledIds.Contains(id)))
        {
            lock (_evaluatorLock)
            {
                _evaluator.ResetRule(id);
            }
        }

        RebuildDeviceIndex();
        _logger.LogInformation("Alert engine loaded {Count} enabled rule(s)", enabled.Count);
    }

    private static bool SameCondition(AlertRuleDefinition a, AlertRuleDefinition b) =>
        a.Type == b.Type && a.Key == b.Key && a.Operator == b.Operator && a.Threshold == b.Threshold &&
        a.ClearThreshold == b.ClearThreshold && a.Duration == b.Duration && a.Scope.ToJson() == b.Scope.ToJson();

    private void RebuildDeviceIndex()
    {
        _worstByDevice = _open.Values
            .Where(a => a.DeviceId.HasValue)
            .GroupBy(a => a.DeviceId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(a => a.Severity));
    }
}
