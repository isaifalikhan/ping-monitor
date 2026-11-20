using Microsoft.EntityFrameworkCore;
using NetWatch.Alerts.Engine;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;

namespace NetWatch.Alerts;

public sealed record AlertQuery(
    IReadOnlyList<AlertState>? States,
    Severity? MinSeverity,
    int? DeviceId,
    string? Search,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int Page = 1,
    int PageSize = 100);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>Who performed an alert action.</summary>
public sealed record AlertActor(int UserId, string Username);

/// <summary>Operator-facing alert operations (list, acknowledge, comment, close). Scoped service.</summary>
public sealed class AlertService
{
    private readonly NetWatchDbContext _db;
    private readonly AlertManager _manager;
    private readonly ILiveUpdatePublisher _live;

    public AlertService(NetWatchDbContext db, AlertManager manager, ILiveUpdatePublisher live)
    {
        _db = db;
        _manager = manager;
        _live = live;
    }

    public async Task<PagedResult<AlertDto>> ListAsync(AlertQuery q, CancellationToken ct)
    {
        var query = _db.Alerts.AsNoTracking().AsQueryable();
        if (q.States is { Count: > 0 })
        {
            var states = q.States.ToList();
            query = query.Where(a => states.Contains(a.State));
        }

        if (q.MinSeverity is { } min)
        {
            var allowed = Enum.GetValues<Severity>().Where(s => s >= min).ToList();
            query = query.Where(a => allowed.Contains(a.Severity));
        }

        if (q.DeviceId is { } deviceId)
        {
            query = query.Where(a => a.DeviceId == deviceId);
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = $"%{q.Search.Trim()}%";
            query = query.Where(a => EF.Functions.Like(a.Title, term) || EF.Functions.Like(a.Message, term));
        }

        if (q.FromUtc is { } from)
        {
            query = query.Where(a => a.LastSeenUtc >= from);
        }

        if (q.ToUtc is { } to)
        {
            query = query.Where(a => a.FirstSeenUtc < to);
        }

        var pageSize = Math.Clamp(q.PageSize, 1, 1000);
        var page = Math.Max(1, q.Page);
        var total = await query.CountAsync(ct);

        // Severity is stored as text, so sort open alerts by severity in memory after paging by time.
        var rows = await query
            .OrderByDescending(a => a.FirstSeenUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var names = await DeviceNamesAsync(rows.Select(r => r.DeviceId), ct);
        var items = rows
            .OrderByDescending(a => a.State is AlertState.Active or AlertState.Acknowledged)
            .ThenByDescending(a => a.Severity)
            .ThenByDescending(a => a.FirstSeenUtc)
            .Select(a => AlertDto.From(a, a.DeviceId is { } d ? names.GetValueOrDefault(d) : null))
            .ToList();
        return new PagedResult<AlertDto>(items, total, page, pageSize);
    }

    public async Task<(AlertDto Alert, IReadOnlyList<AlertHistoryDto> History)?> GetAsync(long id, CancellationToken ct)
    {
        var alert = await _db.Alerts.AsNoTracking().Include(a => a.History).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null)
        {
            return null;
        }

        var name = alert.DeviceId is { } d ? await _db.Devices.Where(x => x.Id == d).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var history = alert.History
            .OrderBy(h => h.TimestampUtc)
            .Select(h => new AlertHistoryDto(h.TimestampUtc, h.Action, h.Username, h.Comment))
            .ToList();
        return (AlertDto.From(alert, name), history);
    }

    /// <summary>Acknowledges Active alerts. Returns the number acknowledged.</summary>
    public async Task<int> AcknowledgeAsync(IReadOnlyCollection<long> ids, AlertActor actor, string? comment, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var alerts = await _db.Alerts.Where(a => ids.Contains(a.Id) && a.State == AlertState.Active).ToListAsync(ct);
        foreach (var alert in alerts)
        {
            alert.State = AlertState.Acknowledged;
            alert.AckByUserId = actor.UserId;
            alert.AckByUsername = actor.Username;
            alert.AckUtc = now;
            alert.AckComment = Trim(comment);
            _db.AlertHistory.Add(new AlertHistoryEntry { AlertId = alert.Id, TimestampUtc = now, Action = "Acknowledged", Username = actor.Username, Comment = Trim(comment) });
        }

        await _db.SaveChangesAsync(ct);
        foreach (var alert in alerts)
        {
            _manager.OnAcknowledged(alert.DedupKey, alert.Id, alert.DeviceId, alert.Severity);
        }

        await PublishAsync(alerts, AlertEvents.Updated, ct);
        return alerts.Count;
    }

    public async Task<bool> CommentAsync(long id, AlertActor actor, string comment, CancellationToken ct)
    {
        if (!await _db.Alerts.AnyAsync(a => a.Id == id, ct))
        {
            return false;
        }

        _db.AlertHistory.Add(new AlertHistoryEntry { AlertId = id, TimestampUtc = DateTime.UtcNow, Action = "Commented", Username = actor.Username, Comment = Trim(comment) });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Closes alerts. Resolved alerts can always be closed. Open alerts are closed only when
    /// <paramref name="includeOpen"/> is set (e.g. event alerts that never auto-resolve, or stale alerts).
    /// </summary>
    public async Task<int> CloseAsync(IReadOnlyCollection<long> ids, AlertActor actor, string? comment, bool includeOpen, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var alerts = await _db.Alerts
            .Where(a => ids.Contains(a.Id) && a.State != AlertState.Closed)
            .ToListAsync(ct);
        alerts = alerts.Where(a => a.State == AlertState.Resolved || includeOpen || !a.AutoResolve).ToList();

        var wasOpen = new List<string>();
        foreach (var alert in alerts)
        {
            if (alert.State is AlertState.Active or AlertState.Acknowledged)
            {
                wasOpen.Add(alert.DedupKey);
            }

            alert.State = AlertState.Closed;
            alert.ClosedUtc = now;
            alert.ClosedByUsername = actor.Username;
            _db.AlertHistory.Add(new AlertHistoryEntry { AlertId = alert.Id, TimestampUtc = now, Action = "Closed", Username = actor.Username, Comment = Trim(comment) });
        }

        await _db.SaveChangesAsync(ct);
        foreach (var key in wasOpen)
        {
            _manager.OnClosed(key);
        }

        await PublishAsync(alerts, AlertEvents.Updated, ct);
        return alerts.Count;
    }

    private async Task PublishAsync(IEnumerable<Alert> alerts, string evt, CancellationToken ct)
    {
        var list = alerts.ToList();
        var names = await DeviceNamesAsync(list.Select(a => a.DeviceId), ct);
        foreach (var alert in list)
        {
            await _live.PublishAsync(evt, AlertDto.From(alert, alert.DeviceId is { } d ? names.GetValueOrDefault(d) : null));
        }
    }

    private async Task<Dictionary<int, string>> DeviceNamesAsync(IEnumerable<int?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        return await _db.Devices.AsNoTracking().Where(d => list.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
    }

    private static string? Trim(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? null : comment.Trim() is { Length: > 2000 } s ? s[..2000] : comment.Trim();
}
