using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Core;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Api;

/// <summary>Users, audit trail and settings (Admin only).</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api").RequireAuthorization(Policies.Admin);
        admin.MapGet("/users", ListUsersAsync);
        admin.MapPost("/users", CreateUserAsync);
        admin.MapPut("/users/{id:int}", UpdateUserAsync);
        admin.MapPost("/users/{id:int}/reset-password", ResetPasswordAsync);
        admin.MapPost("/users/{id:int}/unlock", UnlockAsync);
        admin.MapDelete("/users/{id:int}", DeleteUserAsync);

        admin.MapGet("/audit", AuditAsync);

        admin.MapGet("/settings", GetSettings);
        admin.MapPut("/settings/monitoring", (MonitoringSettings s, HttpContext h, SettingsService svc, AuditWriter a, CancellationToken ct) =>
            SaveAsync(MonitoringSettings.Key, s, x => x.Normalise(), h, svc, a, ct));
        admin.MapPut("/settings/security", (SecuritySettings s, HttpContext h, SettingsService svc, AuditWriter a, CancellationToken ct) =>
            SaveAsync(SecuritySettings.Key, s, x => x.Normalise(), h, svc, a, ct));
        admin.MapPut("/settings/retention", (RetentionSettings s, HttpContext h, SettingsService svc, AuditWriter a, CancellationToken ct) =>
            SaveAsync(RetentionSettings.Key, s, x => x.Normalise(), h, svc, a, ct));
    }

    private static UserDto ToDto(User u) => new(u.Id, u.Username, u.DisplayName, u.Email, u.Role, u.AuthSource, u.IsEnabled,
        u.LockedUntilUtc > DateTime.UtcNow, u.LastLoginUtc, u.MustChangePassword, u.CreatedUtc);

    private static async Task<IResult> ListUsersAsync(NetWatchDbContext db, CancellationToken ct) =>
        Results.Ok((await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct)).Select(ToDto));

    private static async Task<IResult> CreateUserAsync(UserCreate input, HttpContext http, NetWatchDbContext db, PasswordService passwords, SettingsService settings, AuditWriter audit, CancellationToken ct)
    {
        var username = input.Username?.Trim() ?? string.Empty;
        if (username.Length is < 2 or > 128 || username.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return HttpExtensions.Error("User name must be 2–128 characters without spaces.");
        }

        if (await db.Users.AnyAsync(u => u.Username == username, ct))
        {
            return HttpExtensions.Error("This user name already exists.");
        }

        if (PasswordService.Validate(input.Password, username, settings.Security.MinPasswordLength) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        var now = DateTime.UtcNow;
        var user = new User
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? username : input.DisplayName.Trim(),
            Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim(),
            Role = input.Role,
            AuthSource = AuthSource.Local,
            IsEnabled = true,
            MustChangePassword = input.MustChangePassword,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        user.PasswordHash = passwords.Hash(user, input.Password!);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(http.Actor(), "UserCreated", "User", user.Id.Inv(), $"{user.Username} ({user.Role})");
        return Results.Created($"/api/users/{user.Id}", ToDto(user));
    }

    private static async Task<IResult> UpdateUserAsync(int id, UserUpdate input, HttpContext http, NetWatchDbContext db, SessionService sessions, AuditWriter audit, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        var demotesOrDisablesAdmin = user.Role == UserRole.Admin && (input.Role != UserRole.Admin || !input.IsEnabled);
        if (demotesOrDisablesAdmin && await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsEnabled, ct) <= 1)
        {
            return HttpExtensions.Error("At least one enabled administrator must remain.");
        }

        if (id == http.User.UserId() && (!input.IsEnabled || input.Role != user.Role))
        {
            return HttpExtensions.Error("You cannot disable your own account or change your own role.");
        }

        var changes = $"role {user.Role}→{input.Role}, enabled {user.IsEnabled}→{input.IsEnabled}";
        user.DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? user.Username : input.DisplayName.Trim();
        user.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
        var revoke = user.Role != input.Role || (user.IsEnabled && !input.IsEnabled);
        user.Role = input.Role;
        user.IsEnabled = input.IsEnabled;
        user.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (revoke)
        {
            await sessions.RevokeUserAsync(id, null, ct);
        }

        sessions.InvalidateUser(id);
        await audit.WriteAsync(http.Actor(), "UserUpdated", "User", id.Inv(), $"{user.Username}: {changes}");
        return Results.Ok(ToDto(user));
    }

    private static async Task<IResult> ResetPasswordAsync(int id, PasswordReset input, HttpContext http, NetWatchDbContext db, PasswordService passwords, SettingsService settings, SessionService sessions, AuditWriter audit, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (PasswordService.Validate(input.Password, user.Username, settings.Security.MinPasswordLength) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        user.PasswordHash = passwords.Hash(user, input.Password!);
        user.MustChangePassword = input.MustChangePassword;
        user.FailedLoginCount = 0;
        user.LockedUntilUtc = null;
        user.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await sessions.RevokeUserAsync(id, id == http.User.UserId() ? http.User.SessionId() : null, ct);
        sessions.InvalidateUser(id);
        await audit.WriteAsync(http.Actor(), "UserPasswordReset", "User", id.Inv(), user.Username);
        return Results.NoContent();
    }

    private static async Task<IResult> UnlockAsync(int id, HttpContext http, NetWatchDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var count = await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.LockedUntilUtc, (DateTime?)null)
            .SetProperty(u => u.FailedLoginCount, 0), ct);
        if (count == 0)
        {
            return Results.NotFound();
        }

        await audit.WriteAsync(http.Actor(), "UserUnlocked", "User", id.Inv());
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteUserAsync(int id, HttpContext http, NetWatchDbContext db, AuditWriter audit, CancellationToken ct)
    {
        if (id == http.User.UserId())
        {
            return HttpExtensions.Error("You cannot delete your own account.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (user.Role == UserRole.Admin && await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsEnabled, ct) <= 1)
        {
            return HttpExtensions.Error("At least one enabled administrator must remain.");
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(http.Actor(), "UserDeleted", "User", id.Inv(), user.Username);
        return Results.NoContent();
    }

    private static async Task<IResult> AuditAsync(NetWatchDbContext db, DateTime? from, DateTime? to, string? user, string? action, int? page, int? pageSize, CancellationToken ct)
    {
        var query = db.AuditLog.AsNoTracking().AsQueryable();
        if (from is { } f)
        {
            var fu = f.ToUniversalTime();
            query = query.Where(a => a.TimestampUtc >= fu);
        }

        if (to is { } t)
        {
            var tu = t.ToUniversalTime();
            query = query.Where(a => a.TimestampUtc < tu);
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            query = query.Where(a => EF.Functions.Like(a.Username, $"%{user.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => EF.Functions.Like(a.Action, $"%{action.Trim()}%"));
        }

        var size = Math.Clamp(pageSize ?? 100, 1, 1000);
        var p = Math.Max(1, page ?? 1);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.Id).Skip((p - 1) * size).Take(size)
            .Select(a => new AuditDto(a.Id, a.TimestampUtc, a.Username, a.Action, a.TargetType, a.TargetId, a.Details, a.ClientIp))
            .ToListAsync(ct);
        return Results.Ok(new { items, total, page = p, pageSize = size });
    }

    private static IResult GetSettings(SettingsService settings) =>
        Results.Ok(new { monitoring = settings.Monitoring, security = settings.Security, retention = settings.Retention });

    private static async Task<IResult> SaveAsync<T>(string key, T value, Action<T> normalise, HttpContext http, SettingsService settings, AuditWriter audit, CancellationToken ct)
        where T : class
    {
        normalise(value);
        await settings.SaveAsync(key, value, http.User.UserId(), ct);
        await audit.WriteAsync(http.Actor(), "SettingsUpdated", "Settings", key, JsonSerializer.Serialize(value));
        return Results.Ok(value);
    }
}
