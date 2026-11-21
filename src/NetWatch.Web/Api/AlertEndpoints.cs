using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Alerts;
using NetWatch.Alerts.Engine;
using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Api;

/// <summary>Alerts (list/ack/comment/close) and alert rules.</summary>
public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api").RequireAuthorization(Policies.Viewer);
        read.MapGet("/alerts", ListAsync);
        read.MapGet("/alerts/summary", Summary);
        read.MapGet("/alerts/{id:long}", GetAsync);
        read.MapGet("/rules", ListRulesAsync);
        read.MapGet("/rules/keys", RuleKeys);

        var op = app.MapGroup("/api").RequireAuthorization(Policies.Operator);
        op.MapPost("/alerts/ack", AcknowledgeAsync);
        op.MapPost("/alerts/close", CloseAsync);
        op.MapPost("/alerts/{id:long}/comment", CommentAsync);

        var admin = app.MapGroup("/api").RequireAuthorization(Policies.Admin);
        admin.MapPost("/rules", CreateRuleAsync);
        admin.MapPut("/rules/{id:int}", UpdateRuleAsync);
        admin.MapDelete("/rules/{id:int}", DeleteRuleAsync);
    }

    private static async Task<IResult> ListAsync(AlertService alerts, string? state, Severity? minSeverity, int? deviceId, string? search, DateTime? from, DateTime? to, int? page, int? pageSize, CancellationToken ct)
    {
        var states = (state ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<AlertState>(s, true, out var v) ? v : (AlertState?)null)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToList();
        var result = await alerts.ListAsync(new AlertQuery(states, minSeverity, deviceId, search, from?.ToUniversalTime(), to?.ToUniversalTime(), page ?? 1, pageSize ?? 200), ct);
        return Results.Ok(result);
    }

    private static IResult Summary(IActiveAlertIndex index)
    {
        var counts = index.CountBySeverity();
        return Results.Ok(Enum.GetValues<Severity>().ToDictionary(s => s.ToString(), s => counts.GetValueOrDefault(s)));
    }

    private static async Task<IResult> GetAsync(long id, AlertService alerts, CancellationToken ct)
    {
        var result = await alerts.GetAsync(id, ct);
        return result is null ? Results.NotFound() : Results.Ok(new { alert = result.Value.Alert, history = result.Value.History });
    }

    private static async Task<IResult> AcknowledgeAsync(IdsRequest request, HttpContext http, AlertService alerts, AuditWriter audit, CancellationToken ct)
    {
        if (request.Ids is not { Count: > 0 } || request.Ids.Count > 5000)
        {
            return HttpExtensions.Error("Select between 1 and 5000 alerts.");
        }

        var count = await alerts.AcknowledgeAsync(request.Ids, http.AlertActor(), request.Comment, ct);
        await audit.WriteAsync(http.Actor(), "AlertsAcknowledged", "Alert", string.Join(",", request.Ids.Take(50)), request.Comment);
        return Results.Ok(new { updated = count });
    }

    private static async Task<IResult> CloseAsync(IdsRequest request, HttpContext http, AlertService alerts, AuditWriter audit, CancellationToken ct)
    {
        if (request.Ids is not { Count: > 0 } || request.Ids.Count > 5000)
        {
            return HttpExtensions.Error("Select between 1 and 5000 alerts.");
        }

        var count = await alerts.CloseAsync(request.Ids, http.AlertActor(), request.Comment, request.IncludeOpen, ct);
        await audit.WriteAsync(http.Actor(), "AlertsClosed", "Alert", string.Join(",", request.Ids.Take(50)), request.Comment);
        return Results.Ok(new { updated = count });
    }

    private static async Task<IResult> CommentAsync(long id, CommentRequest request, HttpContext http, AlertService alerts, AuditWriter audit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Comment))
        {
            return HttpExtensions.Error("Comment is empty.");
        }

        if (!await alerts.CommentAsync(id, http.AlertActor(), request.Comment, ct))
        {
            return Results.NotFound();
        }

        await audit.WriteAsync(http.Actor(), "AlertCommented", "Alert", id.Inv(), request.Comment);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- rules

    private static RuleDto ToDto(AlertRule r) => new(r.Id, r.Name, r.Description, r.IsEnabled, r.IsBuiltIn, r.RuleType, r.Severity,
        RuleScope.Parse(r.ScopeJson), r.Key, r.Operator, r.Threshold, r.ClearThreshold, r.DurationSec, r.UpdatedUtc,
        EventFilter.Parse(r.FilterJson), r.WindowSec, r.CountThreshold, r.EscalateAfterMin, r.GroupByField);

    /// <summary>Keys an Absence rule can watch: any metric, or syslog/trap reception.</summary>
    private static readonly string[] AbsenceEventKeys = [EventKeys.SyslogMessage, EventKeys.SnmpTrap];

    private static async Task<IResult> ListRulesAsync(NetWatchDbContext db, CancellationToken ct) =>
        Results.Ok((await db.AlertRules.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct)).Select(ToDto));

    private static IResult RuleKeys() => Results.Ok(
        MetricKeys.DisplayNames.Select(kv => new RuleKeyDto(AlertRuleType.Threshold, kv.Key, kv.Value))
            .Concat(StateKeys.DisplayNames.Select(kv => new RuleKeyDto(AlertRuleType.StateChange, kv.Key, kv.Value)))
            .Concat(EventKeys.DisplayNames.Select(kv => new RuleKeyDto(AlertRuleType.EventMatch, kv.Key, kv.Value)))
            .Concat(EventKeys.DisplayNames.Where(kv => kv.Key != EventKeys.DeviceReboot).Select(kv => new RuleKeyDto(AlertRuleType.CountInWindow, kv.Key, kv.Value)))
            .Concat(MetricKeys.DisplayNames.Select(kv => new RuleKeyDto(AlertRuleType.Absence, kv.Key, kv.Value)))
            .Concat(AbsenceEventKeys.Select(k => new RuleKeyDto(AlertRuleType.Absence, k, EventKeys.DisplayNames[k]))));

    private static string? Validate(RuleInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200)
        {
            return "Rule name is required (max 200 characters).";
        }

        var key = input.Key ?? string.Empty;
        var known = input.RuleType switch
        {
            AlertRuleType.Threshold => MetricKeys.DisplayNames.ContainsKey(key),
            AlertRuleType.StateChange => StateKeys.DisplayNames.ContainsKey(key),
            AlertRuleType.EventMatch => EventKeys.DisplayNames.ContainsKey(key),
            AlertRuleType.CountInWindow => EventKeys.DisplayNames.ContainsKey(key) && key != EventKeys.DeviceReboot,
            AlertRuleType.Absence => MetricKeys.DisplayNames.ContainsKey(key) || AbsenceEventKeys.Contains(key),
            _ => false,
        };
        if (!known)
        {
            return "Select a metric, state or event for the rule.";
        }

        if (input.RuleType == AlertRuleType.Threshold && (input.Operator is null || input.Threshold is null || !double.IsFinite(input.Threshold.Value)))
        {
            return "Threshold rules need an operator and a threshold value.";
        }

        if (input.DurationSec is < 0 or > 86_400)
        {
            return "Duration must be between 0 and 86400 seconds.";
        }

        if (input.RuleType == AlertRuleType.Absence && input.DurationSec < 60)
        {
            return "Absence rules need a silence period of at least 60 seconds.";
        }

        if (input.RuleType == AlertRuleType.CountInWindow &&
            (input.CountThreshold is < 2 or > 100_000 || input.WindowSec is < 10 or > 86_400))
        {
            return "Count rules need a count of 2–100000 events and a window of 10–86400 seconds.";
        }

        if (input.GroupByField is { Length: > 64 } || (input.GroupByField is { } g && g.Trim().Any(c => !char.IsLetterOrDigit(c) && c != '_')))
        {
            return "Group-by field must be a field name (letters and digits, max 64).";
        }

        if (input.EscalateAfterMin is < 0 or > 1440)
        {
            return "Escalation must be between 0 and 1440 minutes (empty = global setting).";
        }

        if (input.Filter?.Validate() is { } filterError)
        {
            return filterError;
        }

        return null;
    }

    private static void Apply(AlertRule rule, RuleInput input, bool allowStructuralChange)
    {
        rule.Name = input.Name!.Trim();
        rule.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        rule.IsEnabled = input.IsEnabled;
        rule.Severity = input.Severity;
        rule.ScopeJson = (input.Scope ?? new RuleScope()).ToJson();
        rule.Operator = input.Operator;
        rule.Threshold = input.Threshold;
        rule.ClearThreshold = input.ClearThreshold;
        rule.DurationSec = input.DurationSec;
        rule.FilterJson = input.Filter?.ToJson();
        rule.WindowSec = input.RuleType == AlertRuleType.CountInWindow ? input.WindowSec : 0;
        rule.CountThreshold = input.RuleType == AlertRuleType.CountInWindow ? input.CountThreshold : 0;
        rule.GroupByField = input.RuleType == AlertRuleType.CountInWindow && !string.IsNullOrWhiteSpace(input.GroupByField) ? input.GroupByField.Trim() : null;
        rule.EscalateAfterMin = input.EscalateAfterMin;
        if (allowStructuralChange)
        {
            rule.RuleType = input.RuleType;
            rule.Key = input.Key!;
        }

        rule.UpdatedUtc = DateTime.UtcNow;
    }

    private static async Task<IResult> CreateRuleAsync(RuleInput input, HttpContext http, NetWatchDbContext db, AlertManager manager, AuditWriter audit, CancellationToken ct)
    {
        if (Validate(input) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        var rule = new AlertRule { CreatedUtc = DateTime.UtcNow };
        Apply(rule, input, allowStructuralChange: true);
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(ct);
        manager.InvalidateRules();
        await audit.WriteAsync(http.Actor(), "RuleCreated", "Rule", rule.Id.Inv(), rule.Name);
        return Results.Created($"/api/rules/{rule.Id}", ToDto(rule));
    }

    private static async Task<IResult> UpdateRuleAsync(int id, RuleInput input, HttpContext http, NetWatchDbContext db, AlertManager manager, AuditWriter audit, CancellationToken ct)
    {
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return Results.NotFound();
        }

        // Built-in rules keep their type and key; everything else is editable.
        var effective = rule.IsBuiltIn ? input with { RuleType = rule.RuleType, Key = rule.Key } : input;
        if (Validate(effective) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        Apply(rule, effective, allowStructuralChange: !rule.IsBuiltIn);
        await db.SaveChangesAsync(ct);
        manager.InvalidateRules();
        await audit.WriteAsync(http.Actor(), "RuleUpdated", "Rule", id.Inv(),
            $"{rule.Name}: enabled={rule.IsEnabled}, severity={rule.Severity}, threshold={rule.Threshold}, duration={rule.DurationSec}s");
        return Results.Ok(ToDto(rule));
    }

    private static async Task<IResult> DeleteRuleAsync(int id, HttpContext http, NetWatchDbContext db, AlertManager manager, AuditWriter audit, CancellationToken ct)
    {
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return Results.NotFound();
        }

        if (rule.IsBuiltIn)
        {
            return HttpExtensions.Error("Built-in rules cannot be deleted; disable them instead.");
        }

        db.AlertRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        manager.InvalidateRules();
        await audit.WriteAsync(http.Actor(), "RuleDeleted", "Rule", id.Inv(), rule.Name);
        return Results.NoContent();
    }
}
