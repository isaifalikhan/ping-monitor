using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Collectors.Inventory;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Api;

public sealed record MaintenanceWindowDto(
    int Id,
    string Name,
    bool IsEnabled,
    RuleScope Scope,
    MaintenanceRecurrence Recurrence,
    DateTime? StartUtc,
    DateTime? EndUtc,
    string? StartTimeLocal,
    string? DaysOfWeek,
    string? CronExpression,
    int DurationMin,
    string? Reason,
    string? CreatedBy,
    DateTime UpdatedUtc,
    bool IsActive,
    DateTime? ActiveUntilUtc,
    DateTime? NextStartUtc,
    string Schedule,
    int DevicesInScope);

public sealed record MaintenanceWindowInput(
    string? Name,
    bool IsEnabled,
    RuleScope? Scope,
    MaintenanceRecurrence Recurrence,
    DateTime? StartUtc,
    DateTime? EndUtc,
    string? StartTimeLocal,
    string? DaysOfWeek,
    string? CronExpression,
    int DurationMin,
    string? Reason);

/// <summary>Scheduled and ad-hoc maintenance windows (per device, group, type or tag).</summary>
public static class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/maintenance", ListAsync).RequireAuthorization(Policies.Viewer);
        var admin = app.MapGroup("/api/maintenance").RequireAuthorization(Policies.Admin);
        admin.MapPost("/", CreateAsync);
        admin.MapPut("/{id:int}", UpdateAsync);
        admin.MapDelete("/{id:int}", DeleteAsync);
    }

    private static MaintenanceWindowDto ToDto(MaintenanceWindow w, InventoryCache inventory, DateTime now)
    {
        var scope = RuleScope.Parse(w.ScopeJson);
        var end = w.IsEnabled ? MaintenanceSchedule.CurrentEnd(w, now) : null;
        var inScope = inventory.All.Count(d => scope.Matches(d.Context, interfaceIsImportant: false, isInterfaceSignal: false));
        return new MaintenanceWindowDto(w.Id, w.Name, w.IsEnabled, scope, w.Recurrence, w.StartUtc, w.EndUtc, w.StartTimeLocal, w.DaysOfWeek, w.CronExpression,
            w.DurationMin, w.Reason, w.CreatedBy, w.UpdatedUtc, end is not null, end, MaintenanceSchedule.NextStart(w, now), MaintenanceSchedule.Describe(w), inScope);
    }

    private static async Task<IResult> ListAsync(NetWatchDbContext db, InventoryCache inventory, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await db.MaintenanceWindows.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
        return Results.Ok(rows.Select(w => ToDto(w, inventory, now))
            .OrderByDescending(w => w.IsActive)
            .ThenBy(w => w.NextStartUtc ?? DateTime.MaxValue)
            .ToList());
    }

    private static string? Apply(MaintenanceWindow w, MaintenanceWindowInput input)
    {
        w.Name = input.Name?.Trim() ?? string.Empty;
        w.IsEnabled = input.IsEnabled;
        w.ScopeJson = (input.Scope ?? new RuleScope()).ToJson();
        w.Recurrence = input.Recurrence;
        w.StartUtc = input.StartUtc?.ToUniversalTime();
        w.EndUtc = input.EndUtc?.ToUniversalTime();
        w.StartTimeLocal = string.IsNullOrWhiteSpace(input.StartTimeLocal) ? null : input.StartTimeLocal.Trim();
        w.DaysOfWeek = string.IsNullOrWhiteSpace(input.DaysOfWeek) ? null : input.DaysOfWeek.Trim();
        w.CronExpression = string.IsNullOrWhiteSpace(input.CronExpression) ? null : input.CronExpression.Trim();
        w.DurationMin = input.DurationMin;
        w.Reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        w.UpdatedUtc = DateTime.UtcNow;

        if (w.Recurrence == MaintenanceRecurrence.Once)
        {
            w.StartTimeLocal = null;
            w.DaysOfWeek = null;
            w.CronExpression = null;
            w.DurationMin = 0;
        }

        // An empty scope would silence every device: require an explicit choice.
        var scope = input.Scope ?? new RuleScope();
        if (scope.DeviceIds.Count == 0 && scope.GroupIds.Count == 0 && scope.DeviceTypes.Count == 0 && scope.Tags.Count == 0)
        {
            return "Select the devices, groups, device types or tags the window applies to.";
        }

        return MaintenanceSchedule.Validate(w);
    }

    private static async Task<IResult> CreateAsync(MaintenanceWindowInput input, HttpContext http, NetWatchDbContext db, InventoryCache inventory, AuditWriter audit, CancellationToken ct)
    {
        var w = new MaintenanceWindow { CreatedUtc = DateTime.UtcNow, CreatedBy = http.User.Username() };
        if (Apply(w, input) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        db.MaintenanceWindows.Add(w);
        await db.SaveChangesAsync(ct);
        inventory.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "MaintenanceWindowCreated", "MaintenanceWindow", w.Id.Inv(), $"{w.Name}: {MaintenanceSchedule.Describe(w)}");
        return Results.Created($"/api/maintenance/{w.Id}", ToDto(w, inventory, DateTime.UtcNow));
    }

    private static async Task<IResult> UpdateAsync(int id, MaintenanceWindowInput input, HttpContext http, NetWatchDbContext db, InventoryCache inventory, AuditWriter audit, CancellationToken ct)
    {
        var w = await db.MaintenanceWindows.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null)
        {
            return Results.NotFound();
        }

        if (Apply(w, input) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        await db.SaveChangesAsync(ct);
        inventory.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "MaintenanceWindowUpdated", "MaintenanceWindow", id.Inv(), $"{w.Name}: {MaintenanceSchedule.Describe(w)}, enabled={w.IsEnabled}");
        return Results.Ok(ToDto(w, inventory, DateTime.UtcNow));
    }

    private static async Task<IResult> DeleteAsync(int id, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var w = await db.MaintenanceWindows.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null)
        {
            return Results.NotFound();
        }

        db.MaintenanceWindows.Remove(w);
        await db.SaveChangesAsync(ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "MaintenanceWindowDeleted", "MaintenanceWindow", id.Inv(), w.Name);
        return Results.NoContent();
    }
}
