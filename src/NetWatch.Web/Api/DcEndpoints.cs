using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Collectors.DomainControllers;
using NetWatch.Core;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Auth;

namespace NetWatch.Web.Api;

public sealed record DcSummaryDto(
    int DeviceId, string Name, string IpAddress, DeviceStatus Status, DateTime? LastPollUtc,
    string? Domain, string? Site, string? DcName, bool IsGlobalCatalog, string? Roles, string? PdcEmulator,
    double? ClockOffsetSec, double? SkewToPdcSec, DateTime? LastReplicationCheckUtc, string? ReplicationError, string? SecurityLogError,
    int ReplicationLinks, int ReplicationFailing, int ServicesDown, int Events24h, int FailedLogons24h, int Lockouts24h, int GroupChanges24h);

public sealed record DcOverviewDto(IReadOnlyList<DcSummaryDto> Controllers, bool UseForwardedEvents, string? ForwardedError, DateTime? ForwardedLastReadUtc, long ForwardedEventsRead);

public sealed record DcSecurityEventDto(
    long Id, DateTime TimestampUtc, int DeviceId, string? DeviceName, int EventId, string EventName, string Category, Severity Severity,
    string? SubjectUser, string? TargetUser, string? GroupName, string? ObjectName, string? SourceIp, string? Workstation, int? LogonType,
    string? Status, string? SubStatus, string Message);

public sealed record DcLockoutDto(long Id, DateTime TimestampUtc, int DeviceId, string? DeviceName, string? User, string? CallerComputer,
    int FailuresBefore, string? LastFailureSource, DateTime? UnlockedUtc, string? UnlockedBy);

public sealed record DcGroupChangeDto(long Id, DateTime TimestampUtc, int DeviceId, string? DeviceName, int EventId, bool Added, string? Group, string? Member, string? ChangedBy, bool Privileged);

public sealed record DcReplicationDto(int Id, int DeviceId, string DeviceName, string Partner, string NamingContext, DateTime? LastSuccessUtc, DateTime? LastAttemptUtc,
    int LastResult, int ConsecutiveFailures, string? LastMessage, DateTime LastSeenUtc);

public sealed record DcTopEntry(string Key, int Count, DateTime LastUtc);

public sealed record SavedSearchDto(int Id, string Page, string Name, JsonElement Query, bool IsShared, bool IsOwn, DateTime CreatedUtc);

public sealed record SavedSearchInput(string? Page, string? Name, JsonElement Query, bool IsShared);

/// <summary>Domain controllers: security events, lockouts, privileged group changes, replication and saved searches.</summary>
public static class DcEndpoints
{
    private static readonly int[] GroupEventIds = [4728, 4729, 4732, 4733, 4756, 4757];
    private static readonly int[] FailureEventIds = [4625, 4771, 4776, 4768];

    public static void MapDcEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/dc").RequireAuthorization(Policies.Viewer);
        read.MapGet("/summary", SummaryAsync);
        read.MapGet("/security-events", SecurityEventsAsync);
        read.MapGet("/lockouts", LockoutsAsync);
        read.MapGet("/group-changes", GroupChangesAsync);
        read.MapGet("/replication", ReplicationAsync);
        read.MapGet("/top-failures", TopFailuresAsync);
        read.MapGet("/event-ids", () => Results.Ok(DcEventCatalog.Events.OrderBy(e => e.Key).Select(e => new { id = e.Key, name = e.Value.Name, category = e.Value.Category.ToString() })));

        var searches = app.MapGroup("/api/saved-searches").RequireAuthorization(Policies.Viewer);
        searches.MapGet("/", ListSearchesAsync);
        searches.MapPost("/", CreateSearchAsync);
        searches.MapDelete("/{id:int}", DeleteSearchAsync);
    }

    // ---------------------------------------------------------------- summary

    private static async Task<IResult> SummaryAsync(NetWatchDbContext db, EventsDbContext events, SettingsService settings, ForwardedEventsReader forwarded, CancellationToken ct)
    {
        var dcs = await db.Devices.AsNoTracking().Where(d => d.Type == DeviceType.DomainController).OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.IpAddress, d.Status, d.LastPollUtc, Down = d.WatchedServices.Count(w => w.LastState != ServiceState.Running && w.LastState != ServiceState.Unknown) })
            .ToListAsync(ct);
        var ids = dcs.Select(d => d.Id).ToList();
        var infos = await db.DcInfos.AsNoTracking().Where(i => ids.Contains(i.DeviceId)).ToDictionaryAsync(i => i.DeviceId, ct);
        var links = await db.DcReplicationLinks.AsNoTracking().Where(l => ids.Contains(l.DeviceId))
            .GroupBy(l => l.DeviceId).Select(g => new { g.Key, Total = g.Count(), Failing = g.Count(l => l.LastResult != 0 && l.ConsecutiveFailures > 0) })
            .ToDictionaryAsync(x => x.Key, ct);

        var since = DateTime.UtcNow.AddHours(-24);
        var counts = await events.DcSecurityEvents.AsNoTracking().Where(e => e.TimestampUtc >= since)
            .GroupBy(e => e.DeviceId)
            .Select(g => new
            {
                g.Key,
                Total = g.Count(),
                Failed = g.Count(e => e.Category == nameof(DcEventCategory.LogonFailure) || e.EventId == 4771 || ((e.EventId == 4776 || e.EventId == 4768) && e.Status != null && e.Status != "0x0")),
                Lockouts = g.Count(e => e.EventId == 4740),
                Groups = g.Count(e => GroupEventIds.Contains(e.EventId)),
            })
            .ToDictionaryAsync(x => x.Key, ct);

        var list = dcs.Select(d =>
        {
            var i = infos.GetValueOrDefault(d.Id);
            var l = links.GetValueOrDefault(d.Id);
            var c = counts.GetValueOrDefault(d.Id);
            return new DcSummaryDto(d.Id, d.Name, d.IpAddress, d.Status, d.LastPollUtc, i?.Domain, i?.Site, i?.DcName, i?.IsGlobalCatalog ?? false, i?.Roles, i?.PdcEmulator,
                i?.ClockOffsetSec, i?.SkewToPdcSec, i?.LastReplicationCheckUtc, i?.ReplicationError, i?.SecurityLogError,
                l?.Total ?? 0, l?.Failing ?? 0, d.Down, c?.Total ?? 0, c?.Failed ?? 0, c?.Lockouts ?? 0, c?.Groups ?? 0);
        }).ToList();

        var s = settings.Dc;
        return Results.Ok(new DcOverviewDto(list, s.UseForwardedEvents, s.UseForwardedEvents ? forwarded.LastError : null, forwarded.LastReadUtc, forwarded.EventsRead));
    }

    // ---------------------------------------------------------------- security events

    /// <param name="eventIds">Comma-separated event IDs.</param>
    /// <param name="user">Substring of the target or subject account.</param>
    /// <param name="source">Substring of the source IP or workstation.</param>
    private static async Task<IResult> SecurityEventsAsync(
        EventsDbContext events, NetWatchDbContext db, int? deviceId, string? eventIds, string? category, string? user, string? source, string? group,
        string? search, Severity? minSeverity, bool? failedOnly, DateTime? from, DateTime? to, int? page, int? pageSize, CancellationToken ct)
    {
        var q = events.DcSecurityEvents.AsNoTracking().AsQueryable();
        if (deviceId is { } d)
        {
            q = q.Where(x => x.DeviceId == d);
        }

        var ids = ParseIds(eventIds);
        if (ids.Count > 0)
        {
            q = q.Where(x => ids.Contains(x.EventId));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var c = category.Trim();
            q = q.Where(x => x.Category == c);
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            var term = Like(user);
            q = q.Where(x => EF.Functions.Like(x.TargetUser ?? string.Empty, term) || EF.Functions.Like(x.SubjectUser ?? string.Empty, term));
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            var term = Like(source);
            q = q.Where(x => EF.Functions.Like(x.SourceIp ?? string.Empty, term) || EF.Functions.Like(x.Workstation ?? string.Empty, term));
        }

        if (!string.IsNullOrWhiteSpace(group))
        {
            var term = Like(group);
            q = q.Where(x => EF.Functions.Like(x.GroupName ?? string.Empty, term));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = Like(search);
            q = q.Where(x => EF.Functions.Like(x.Message, term));
        }

        if (minSeverity is { } sev)
        {
            q = q.Where(x => x.Severity >= sev);
        }

        if (failedOnly == true)
        {
            q = q.Where(x => x.EventId == 4625 || x.EventId == 4771 || ((x.EventId == 4776 || x.EventId == 4768) && x.Status != null && x.Status != "0x0"));
        }

        if (from is { } f)
        {
            var fu = f.ToUniversalTime();
            q = q.Where(x => x.TimestampUtc >= fu);
        }

        if (to is { } t)
        {
            var tu = t.ToUniversalTime();
            q = q.Where(x => x.TimestampUtc < tu);
        }

        var (p, size) = (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 200, 1, 2000));
        var total = await q.Take(10_001).CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var names = await NamesAsync(db, rows.Select(r => r.DeviceId), ct);
        var items = rows.Select(r => new DcSecurityEventDto(r.Id, r.TimestampUtc, r.DeviceId, names.GetValueOrDefault(r.DeviceId), r.EventId, DcEventCatalog.NameOf(r.EventId),
            r.Category, r.Severity, r.SubjectUser, r.TargetUser, r.GroupName, r.ObjectName, r.SourceIp, r.Workstation, r.LogonType, r.Status, r.SubStatus, r.Message)).ToList();
        return Results.Ok(new { items, total, page = p, pageSize = size, totalCapped = total > 10_000 });
    }

    // ---------------------------------------------------------------- lockouts

    private static async Task<IResult> LockoutsAsync(EventsDbContext events, NetWatchDbContext db, int? days, string? user, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 365));
        var q = events.DcSecurityEvents.AsNoTracking().Where(e => e.EventId == 4740 && e.TimestampUtc >= since);
        if (!string.IsNullOrWhiteSpace(user))
        {
            var term = Like(user);
            q = q.Where(e => EF.Functions.Like(e.TargetUser ?? string.Empty, term));
        }

        var lockouts = await q.OrderByDescending(e => e.TimestampUtc).Take(1000).ToListAsync(ct);
        var users = lockouts.Select(l => l.TargetUser).Where(u => u != null).Distinct().ToList();
        var unlocks = await events.DcSecurityEvents.AsNoTracking().Where(e => e.EventId == 4767 && e.TimestampUtc >= since && users.Contains(e.TargetUser))
            .Select(e => new { e.TargetUser, e.TimestampUtc, e.SubjectUser }).ToListAsync(ct);
        var failureSince = since.AddMinutes(-30);
        var failures = await events.DcSecurityEvents.AsNoTracking()
            .Where(e => (e.EventId == 4625 || e.EventId == 4771 || e.EventId == 4776) && e.TimestampUtc >= failureSince && users.Contains(e.TargetUser))
            .Where(e => e.EventId != 4776 || (e.Status != null && e.Status != "0x0"))
            .Select(e => new { e.TargetUser, e.TimestampUtc, e.SourceIp, e.Workstation }).ToListAsync(ct);
        var names = await NamesAsync(db, lockouts.Select(l => l.DeviceId), ct);

        var items = lockouts.Select(l =>
        {
            var before = failures.Where(f => Same(f.TargetUser, l.TargetUser) && f.TimestampUtc <= l.TimestampUtc && f.TimestampUtc >= l.TimestampUtc.AddMinutes(-30)).ToList();
            var unlock = unlocks.Where(u => Same(u.TargetUser, l.TargetUser) && u.TimestampUtc >= l.TimestampUtc).OrderBy(u => u.TimestampUtc).FirstOrDefault();
            var last = before.OrderByDescending(f => f.TimestampUtc).FirstOrDefault();
            return new DcLockoutDto(l.Id, l.TimestampUtc, l.DeviceId, names.GetValueOrDefault(l.DeviceId), l.TargetUser, l.Workstation,
                before.Count, last is null ? null : last.SourceIp ?? last.Workstation, unlock?.TimestampUtc, unlock?.SubjectUser);
        }).ToList();
        return Results.Ok(items);
    }

    // ---------------------------------------------------------------- group changes

    private static async Task<IResult> GroupChangesAsync(EventsDbContext events, NetWatchDbContext db, SettingsService settings, int? days, bool? privilegedOnly, string? group, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 30, 1, 365));
        var q = events.DcSecurityEvents.AsNoTracking().Where(e => GroupEventIds.Contains(e.EventId) && e.TimestampUtc >= since);
        if (!string.IsNullOrWhiteSpace(group))
        {
            var term = Like(group);
            q = q.Where(e => EF.Functions.Like(e.GroupName ?? string.Empty, term));
        }

        var s = settings.Dc;
        if (privilegedOnly == true)
        {
            var groups = s.PrivilegedGroups;
            q = q.Where(e => e.GroupName != null && groups.Contains(e.GroupName));
        }

        var rows = await q.OrderByDescending(e => e.TimestampUtc).Take(2000).ToListAsync(ct);
        var names = await NamesAsync(db, rows.Select(r => r.DeviceId), ct);
        return Results.Ok(rows.Select(r => new DcGroupChangeDto(r.Id, r.TimestampUtc, r.DeviceId, names.GetValueOrDefault(r.DeviceId), r.EventId,
            DcEventCatalog.GroupAdds.Contains(r.EventId), r.GroupName, r.TargetUser, r.SubjectUser, s.IsPrivilegedGroup(r.GroupName))));
    }

    // ---------------------------------------------------------------- replication

    private static async Task<IResult> ReplicationAsync(NetWatchDbContext db, CancellationToken ct)
    {
        var rows = await db.DcReplicationLinks.AsNoTracking()
            .Join(db.Devices.AsNoTracking(), l => l.DeviceId, d => d.Id, (l, d) => new { l, d.Name })
            .OrderBy(x => x.Name).ThenBy(x => x.l.Partner).ThenBy(x => x.l.NamingContext)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(x => new DcReplicationDto(x.l.Id, x.l.DeviceId, x.Name, x.l.Partner, x.l.NamingContext, x.l.LastSuccessUtc, x.l.LastAttemptUtc,
            x.l.LastResult, x.l.ConsecutiveFailures, x.l.LastMessage, x.l.LastSeenUtc)));
    }

    // ---------------------------------------------------------------- top failures

    private static async Task<IResult> TopFailuresAsync(EventsDbContext events, int? hours, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 31));
        var failed = events.DcSecurityEvents.AsNoTracking()
            .Where(e => e.TimestampUtc >= since && FailureEventIds.Contains(e.EventId))
            .Where(e => e.EventId == 4625 || e.EventId == 4771 || (e.Status != null && e.Status != "0x0"));
        // Order on an anonymous projection: EF cannot translate ordering by a record constructor argument.
        var accounts = await failed.Where(e => e.TargetUser != null).GroupBy(e => e.TargetUser!)
            .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(e => e.TimestampUtc) }).OrderByDescending(x => x.Count).Take(10).ToListAsync(ct);
        var sources = await failed.Where(e => e.SourceIp != null || e.Workstation != null).GroupBy(e => e.SourceIp ?? e.Workstation!)
            .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(e => e.TimestampUtc) }).OrderByDescending(x => x.Count).Take(10).ToListAsync(ct);
        return Results.Ok(new
        {
            accounts = accounts.Select(x => new DcTopEntry(x.Key, x.Count, x.Last)),
            sources = sources.Select(x => new DcTopEntry(x.Key, x.Count, x.Last)),
        });
    }

    // ---------------------------------------------------------------- saved searches

    private static async Task<IResult> ListSearchesAsync(NetWatchDbContext db, ClaimsPrincipal user, string? page, CancellationToken ct)
    {
        var uid = user.UserId();
        var q = db.SavedSearches.AsNoTracking().Where(s => s.UserId == uid || s.IsShared);
        if (!string.IsNullOrWhiteSpace(page))
        {
            q = q.Where(s => s.Page == page);
        }

        var rows = await q.OrderBy(s => s.Name).ToListAsync(ct);
        return Results.Ok(rows.Select(s => ToDto(s, uid)));
    }

    private static async Task<IResult> CreateSearchAsync(NetWatchDbContext db, ClaimsPrincipal user, SavedSearchInput input, CancellationToken ct)
    {
        var name = input.Name?.Trim();
        var page = input.Page?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100 || string.IsNullOrEmpty(page) || page.Length > 32)
        {
            return Results.BadRequest(new { error = "A name (max 100 characters) and page are required." });
        }

        if (input.Query.ValueKind != JsonValueKind.Object)
        {
            return Results.BadRequest(new { error = "The search must be a JSON object." });
        }

        var json = input.Query.GetRawText();
        if (json.Length > 4000)
        {
            return Results.BadRequest(new { error = "The search is too large." });
        }

        // Shared searches are visible to everyone: only operators and admins may publish them.
        if (input.IsShared && !Policies.OperatorRoles.Any(user.IsInRole))
        {
            return Results.Forbid();
        }

        var uid = user.UserId();
        if (await db.SavedSearches.CountAsync(s => s.UserId == uid, ct) >= 100)
        {
            return Results.BadRequest(new { error = "At most 100 saved searches per user." });
        }

        var row = new SavedSearch { UserId = uid, Page = page, Name = name, QueryJson = json, IsShared = input.IsShared, CreatedUtc = DateTime.UtcNow };
        db.SavedSearches.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(row, uid));
    }

    private static async Task<IResult> DeleteSearchAsync(NetWatchDbContext db, ClaimsPrincipal user, int id, CancellationToken ct)
    {
        var row = await db.SavedSearches.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        // Owners delete their own; admins may remove shared searches of others.
        if (row.UserId != user.UserId() && !(row.IsShared && user.IsInRole(nameof(UserRole.Admin))))
        {
            return Results.Forbid();
        }

        db.SavedSearches.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static SavedSearchDto ToDto(SavedSearch s, int uid)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(s.QueryJson) ? "{}" : s.QueryJson);
        return new SavedSearchDto(s.Id, s.Page, s.Name, doc.RootElement.Clone(), s.IsShared, s.UserId == uid, s.CreatedUtc);
    }

    // ---------------------------------------------------------------- helpers

    private static List<int> ParseIds(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1)
                .Where(v => v >= 0)
                .Distinct()
                .Take(50)
                .ToList();

    private static string Like(string text) => $"%{text.Trim()}%";

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<int, string>> NamesAsync(NetWatchDbContext db, IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? new Dictionary<int, string>() : await db.Devices.AsNoTracking().Where(d => list.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
    }
}
