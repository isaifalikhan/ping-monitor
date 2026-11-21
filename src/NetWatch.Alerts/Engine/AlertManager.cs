using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Alerts.Notifications;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;

namespace NetWatch.Alerts.Engine;

/// <summary>
/// Hosts the alert engine: consumes signals from collectors and receivers, evaluates rules, persists alert
/// transitions, applies flapping protection and escalation, and pushes changes to browsers and notification
/// channels. Signals and periodic checks are processed by a single loop, so evaluator state and flapping state
/// need no locking (except for manual resets from the API, which take the evaluator lock).
/// </summary>
public sealed class AlertManager : BackgroundService, IAlertSignalSink, IActiveAlertIndex
{
    private const int BatchSize = 1000;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceContextProvider _devices;
    private readonly IDependencyStatus _dependencies;
    private readonly ILiveUpdatePublisher _live;
    private readonly INotificationSink _notifications;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertManager> _logger;
    private readonly Channel<AlertSignal> _signals;
    private readonly AlertEvaluator _evaluator = new();
    private readonly object _evaluatorLock = new();

    /// <summary>Open (Active/Acknowledged) alerts by dedup key.</summary>
    private readonly ConcurrentDictionary<string, OpenAlert> _open = new(StringComparer.Ordinal);

    // Flapping protection (loop thread only): raise/resolve timestamps per dedup key, and resolves on hold.
    private readonly Dictionary<string, List<DateTime>> _transitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _heldResolves = new(StringComparer.Ordinal);

    private volatile IReadOnlyList<AlertRuleDefinition> _rules = Array.Empty<AlertRuleDefinition>();
    private volatile bool _rulesDirty = true;
    private volatile IReadOnlyDictionary<int, Severity> _worstByDevice = new Dictionary<int, Severity>();
    private volatile IReadOnlyList<(int DeviceId, int RuleId, string Reason)> _suppressed = Array.Empty<(int, int, string)>();
    private long _droppedSignals;
    private long _processedSignals;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record OpenAlert(long Id, int? DeviceId, int? RuleId, Severity Severity, AlertState State, DateTime FirstSeenUtc, bool Escalated);

    public AlertManager(
        IServiceScopeFactory scopeFactory,
        IDeviceContextProvider devices,
        IDependencyStatus dependencies,
        ILiveUpdatePublisher live,
        INotificationSink notifications,
        SettingsService settings,
        TimeProvider time,
        ILogger<AlertManager> logger)
    {
        _scopeFactory = scopeFactory;
        _devices = devices;
        _dependencies = dependencies;
        _live = live;
        _notifications = notifications;
        _settings = settings;
        _time = time;
        _logger = logger;
        _signals = Channel.CreateBounded<AlertSignal>(
            new BoundedChannelOptions(200_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _droppedSignals));
    }

    public long DroppedSignals => Interlocked.Read(ref _droppedSignals);

    public long ProcessedSignals => Interlocked.Read(ref _processedSignals);

    public int QueuedSignals => _signals.Reader.CanCount ? _signals.Reader.Count : -1;

    /// <summary>Conditions currently suppressed by maintenance or an unreachable upstream device.</summary>
    public IReadOnlyList<(int DeviceId, int RuleId, string Reason)> SuppressedConditions => _suppressed;

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
        if (_open.TryGetValue(dedupKey, out var open))
        {
            _open[dedupKey] = open with { State = AlertState.Acknowledged };
        }
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
        var nextTick = _time.GetUtcNow().UtcDateTime + TickInterval;
        Task<bool>? waiting = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for signals, but wake up for the periodic checks (escalation, absence, flapping).
                waiting ??= reader.WaitToReadAsync(stoppingToken).AsTask();
                var timeout = nextTick - _time.GetUtcNow().UtcDateTime;
                if (timeout > TimeSpan.Zero)
                {
                    await Task.WhenAny(waiting, Task.Delay(timeout, _time, stoppingToken));
                }

                if (waiting.IsCompleted)
                {
                    if (!await waiting)
                    {
                        break;
                    }

                    waiting = null;
                    batch.Clear();
                    while (batch.Count < BatchSize && reader.TryRead(out var signal))
                    {
                        batch.Add(signal);
                    }

                    if (_rulesDirty)
                    {
                        await ReloadRulesAsync(stoppingToken);
                    }

                    await ProcessBatchAsync(batch, stoppingToken);
                }

                if (_time.GetUtcNow().UtcDateTime >= nextTick)
                {
                    nextTick = _time.GetUtcNow().UtcDateTime + TickInterval;
                    if (_rulesDirty)
                    {
                        await ReloadRulesAsync(stoppingToken);
                    }

                    await TickAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alert engine iteration failed ({Count} signal(s) in batch)", batch.Count);
            }
        }
    }

    private string? Upstream(int deviceId) =>
        _settings.Alerting.DependencySuppression ? _dependencies.FindUnreachableUpstream(deviceId) : null;

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

                transitions.AddRange(_evaluator.Evaluate(signal, device, rules, Upstream(signal.DeviceId)));
            }
        }

        Interlocked.Add(ref _processedSignals, batch.Count);
        if (transitions.Count > 0)
        {
            await PersistAsync(transitions, ct);
        }
    }

    /// <summary>Periodic work: time-based rules, held (flapping) resolves, escalation.</summary>
    private async Task TickAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        IReadOnlyList<AlertTransition> transitions;
        lock (_evaluatorLock)
        {
            transitions = _evaluator.Tick(now, _devices.Get, _rules, Upstream);
            _suppressed = _evaluator.Suppressed;
        }

        if (transitions.Count > 0)
        {
            await PersistAsync(transitions, ct);
        }

        await ReleaseStableFlappersAsync(now, ct);
        await EscalateAsync(now, ct);

        // Forget flap history that has left the window.
        var window = TimeSpan.FromMinutes(_settings.Alerting.FlapWindowMin);
        foreach (var key in _transitions.Keys.ToList())
        {
            var list = _transitions[key];
            list.RemoveAll(t => now - t > window);
            if (list.Count == 0)
            {
                _transitions.Remove(key);
            }
        }
    }

    private async Task PersistAsync(IReadOnlyList<AlertTransition> transitions, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var notifications = new List<(string Event, Alert Alert, NotificationKind? Notify)>();
        var alerting = _settings.Alerting;

        foreach (var t in transitions)
        {
            if (t.Kind == TransitionKind.Raise)
            {
                var wasHeld = _heldResolves.Remove(t.DedupKey);
                if (!_open.ContainsKey(t.DedupKey) || wasHeld)
                {
                    RecordTransition(t.DedupKey, t.TimestampUtc, alerting);
                }

                var alert = await RaiseAsync(db, t, ct);
                if (alert is not null)
                {
                    var isNew = alert.OccurrenceCount == 1;
                    notifications.Add((isNew ? AlertEvents.Raised : AlertEvents.Updated, alert, isNew ? NotificationKind.Raised : null));
                }
            }
            else
            {
                if (!_open.ContainsKey(t.DedupKey))
                {
                    continue;
                }

                var count = RecordTransition(t.DedupKey, t.TimestampUtc, alerting);
                if (count >= alerting.FlapThreshold)
                {
                    // Flapping: keep the alert open until the condition has been stable for a while.
                    var held = await HoldResolveAsync(db, t, count, ct);
                    if (held is not null)
                    {
                        notifications.Add((AlertEvents.Updated, held, null));
                    }

                    continue;
                }

                var alert = await ResolveAsync(db, t.DedupKey, t.TimestampUtc, t.Value, t.Message, ct);
                if (alert is not null)
                {
                    notifications.Add((AlertEvents.Resolved, alert, NotificationKind.Resolved));
                }
            }
        }

        RebuildDeviceIndex();
        await PublishAsync(notifications);
    }

    private async Task PublishAsync(IEnumerable<(string Event, Alert Alert, NotificationKind? Notify)> items)
    {
        foreach (var (evt, alert, notify) in items)
        {
            var name = alert.DeviceId is { } id ? _devices.Get(id)?.Name : null;
            var dto = AlertDto.From(alert, name);
            await _live.PublishAsync(evt, dto);
            if (notify is { } kind)
            {
                _notifications.Enqueue(new AlertNotification(kind, dto));
            }
        }
    }

    /// <summary>Adds a state change to the flap history of an alert and returns the number of changes in the window.</summary>
    private int RecordTransition(string key, DateTime ts, AlertingSettings alerting)
    {
        if (!_transitions.TryGetValue(key, out var list))
        {
            list = new List<DateTime>();
            _transitions[key] = list;
        }

        list.Add(ts);
        var window = TimeSpan.FromMinutes(alerting.FlapWindowMin);
        list.RemoveAll(t => ts - t > window);
        return list.Count;
    }

    private async Task<Alert?> RaiseAsync(NetWatchDbContext db, AlertTransition t, CancellationToken ct)
    {
        if (_open.TryGetValue(t.DedupKey, out var open))
        {
            // Already open (repeated event, or the condition came back while a flapping resolve was held).
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
            // Unique open-alert index violated (another writer opened it meanwhile), or the device was deleted while
            // its signals were queued (foreign key). Log and skip; the engine continues.
            _logger.LogWarning(ex, "Alert {DedupKey} not stored: an open alert already exists or device {DeviceId} was deleted", t.DedupKey, t.DeviceId);
            db.ChangeTracker.Clear();
            return null;
        }

        _open[t.DedupKey] = new OpenAlert(alert.Id, alert.DeviceId, alert.RuleId, alert.Severity, AlertState.Active, alert.FirstSeenUtc, Escalated: false);
        _logger.LogInformation("Alert raised [{Severity}] {Title}: {Message}", alert.Severity, alert.Title, alert.Message);
        return alert;
    }

    private async Task<Alert?> HoldResolveAsync(NetWatchDbContext db, AlertTransition t, int changes, CancellationToken ct)
    {
        if (!_open.TryGetValue(t.DedupKey, out var open))
        {
            return null;
        }

        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == open.Id, ct);
        if (alert is null)
        {
            return null;
        }

        _heldResolves[t.DedupKey] = t.TimestampUtc;
        alert.LastSeenUtc = t.TimestampUtc;
        alert.FlapCount = changes;
        if (!alert.IsFlapping)
        {
            alert.IsFlapping = true;
            db.AlertHistory.Add(new AlertHistoryEntry
            {
                AlertId = alert.Id,
                TimestampUtc = t.TimestampUtc,
                Action = "Flapping",
                Comment = $"{changes} state changes in {_settings.Alerting.FlapWindowMin} min; the alert stays open until the condition is stable for {_settings.Alerting.FlapStableMin} min.",
            });
            _logger.LogInformation("Alert {Title} is flapping ({Changes} changes)", alert.Title, changes);
        }

        await db.SaveChangesAsync(ct);
        return alert;
    }

    private async Task<Alert?> ResolveAsync(NetWatchDbContext db, string dedupKey, DateTime ts, double? value, string message, CancellationToken ct)
    {
        if (!_open.TryRemove(dedupKey, out var open))
        {
            return null;
        }

        _heldResolves.Remove(dedupKey);
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == open.Id, ct);
        if (alert is null || alert.State is AlertState.Resolved or AlertState.Closed)
        {
            return null;
        }

        alert.State = AlertState.Resolved;
        alert.ResolvedUtc = ts;
        alert.LastSeenUtc = ts;
        alert.IsFlapping = false;
        alert.CurrentValue = value ?? alert.CurrentValue;
        db.AlertHistory.Add(new AlertHistoryEntry { AlertId = alert.Id, TimestampUtc = ts, Action = "Resolved", Comment = message });
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Alert resolved {Title}: {Message}", alert.Title, message);
        return alert;
    }

    /// <summary>Resolves held (flapping) alerts whose condition has stayed clear for the stable period.</summary>
    private async Task ReleaseStableFlappersAsync(DateTime now, CancellationToken ct)
    {
        if (_heldResolves.Count == 0)
        {
            return;
        }

        var stable = TimeSpan.FromMinutes(_settings.Alerting.FlapStableMin);
        var due = _heldResolves.Where(kv => now - kv.Value >= stable || !_open.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        if (due.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var resolved = new List<(string, Alert, NotificationKind?)>();
        foreach (var key in due)
        {
            _heldResolves.Remove(key);
            var alert = await ResolveAsync(db, key, now, null, $"Condition stable for {_settings.Alerting.FlapStableMin} min after flapping.", ct);
            if (alert is not null)
            {
                resolved.Add((AlertEvents.Resolved, alert, NotificationKind.Resolved));
            }
        }

        RebuildDeviceIndex();
        await PublishAsync(resolved);
    }

    /// <summary>Flags unacknowledged alerts that exceeded their escalation time; browsers repeat the alarm sound for them.</summary>
    private async Task EscalateAsync(DateTime now, CancellationToken ct)
    {
        var alerting = _settings.Alerting;
        var rules = _rules.ToDictionary(r => r.Id);
        var due = _open.Where(kv =>
        {
            var a = kv.Value;
            if (a.State != AlertState.Active || a.Escalated)
            {
                return false;
            }

            var minutes = a.RuleId is { } rid && rules.TryGetValue(rid, out var rule) && rule.EscalateAfterMin is { } m
                ? m
                : a.Severity switch
                {
                    Severity.Critical => alerting.EscalateCriticalAfterMin,
                    Severity.Major => alerting.EscalateMajorAfterMin,
                    _ => 0,
                };
            return minutes > 0 && now - a.FirstSeenUtc >= TimeSpan.FromMinutes(minutes);
        }).ToList();

        if (due.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var escalated = new List<(string, Alert, NotificationKind?)>();
        foreach (var (key, open) in due)
        {
            _open[key] = open with { Escalated = true };
            var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == open.Id, ct);
            if (alert is null || alert.State != AlertState.Active)
            {
                continue;
            }

            alert.EscalatedUtc = now;
            var minutes = (int)Math.Round((now - alert.FirstSeenUtc).TotalMinutes);
            db.AlertHistory.Add(new AlertHistoryEntry { AlertId = alert.Id, TimestampUtc = now, Action = "Escalated", Comment = $"Not acknowledged after {minutes} min." });
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Alert escalated [{Severity}] {Title}: not acknowledged after {Minutes} min", alert.Severity, alert.Title, minutes);
            escalated.Add((AlertEvents.Escalated, alert, NotificationKind.Escalated));
        }

        await PublishAsync(escalated);
    }

    private async Task LoadOpenAlertsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var open = await db.Alerts.AsNoTracking()
            .Where(a => a.State == AlertState.Active || a.State == AlertState.Acknowledged)
            .Select(a => new { a.Id, a.DedupKey, a.DeviceId, a.RuleId, a.Severity, a.State, a.AutoResolve, a.FirstSeenUtc, a.EscalatedUtc })
            .ToListAsync(ct);

        lock (_evaluatorLock)
        {
            foreach (var a in open)
            {
                _open[a.DedupKey] = new OpenAlert(a.Id, a.DeviceId, a.RuleId, a.Severity, a.State, a.FirstSeenUtc, a.EscalatedUtc.HasValue);
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
                if (previous.TryGetValue(rule.Id, out var old) && !old.SameCondition(rule))
                {
                    _evaluator.ResetRule(rule.Id);
                    foreach (var key in _open.Keys.Where(k => AlertEvaluator.TryParseRuleId(k, out var id) && id == rule.Id))
                    {
                        _evaluator.MarkRaised(key);
                    }
                }
            }

            foreach (var id in previous.Keys.Where(id => !enabledIds.Contains(id)))
            {
                _evaluator.ResetRule(id);
            }
        }

        var orphaned = _open
            .Where(kv => AlertEvaluator.TryParseRuleId(kv.Key, out var id) && !enabledIds.Contains(id))
            .Select(kv => kv.Key)
            .ToList();
        var resolved = new List<(string, Alert, NotificationKind?)>();
        foreach (var key in orphaned)
        {
            var alert = await ResolveAsync(db, key, _time.GetUtcNow().UtcDateTime, null, "Rule disabled or deleted", ct);
            if (alert is not null)
            {
                resolved.Add((AlertEvents.Resolved, alert, null));
            }
        }

        RebuildDeviceIndex();
        await PublishAsync(resolved);
        _logger.LogInformation("Alert engine loaded {Count} enabled rule(s)", enabled.Count);
    }

    private void RebuildDeviceIndex()
    {
        _worstByDevice = _open.Values
            .Where(a => a.DeviceId.HasValue)
            .GroupBy(a => a.DeviceId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(a => a.Severity));
    }
}
