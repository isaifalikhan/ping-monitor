using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Data.Config;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Auth;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record CurrentUserDto(int Id, string Username, string DisplayName, string Role, bool MustChangePassword, int SessionIdleTimeoutMin);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");
        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Login);
        group.MapPost("/logout", LogoutAsync).AllowAnonymous();
        group.MapGet("/me", MeAsync).RequireAuthorization(Policies.Viewer);
        group.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization(Policies.Viewer);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        NetWatchDbContext db,
        PasswordService passwords,
        SessionService sessions,
        SettingsService settings,
        AuditWriter audit,
        CancellationToken ct)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        var ip = http.Connection.RemoteIpAddress?.ToString();
        if (username.Length == 0 || password.Length == 0 || username.Length > 128 || password.Length > 256)
        {
            return Results.Json(new { error = "Enter user name and password." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var security = settings.Security;
        var now = DateTime.UtcNow;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

        if (user is { LockedUntilUtc: { } lockedUntil } && lockedUntil > now)
        {
            passwords.Verify(null, password);
            await audit.WriteAsync(new AuditActor(user.Id, user.Username, ip), "LoginBlockedLocked");
            return Results.Json(new { error = $"Account is locked after too many failed attempts. Try again after {(int)Math.Ceiling((lockedUntil - now).TotalMinutes)} minute(s) or ask an administrator to unlock it." }, statusCode: StatusCodes.Status423Locked);
        }

        var (valid, needsRehash) = passwords.Verify(user, password);
        if (user is null || !valid || !user.IsEnabled)
        {
            if (user is not null)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= security.MaxFailedLogins)
                {
                    user.LockedUntilUtc = now.AddMinutes(security.LockoutMinutes);
                    user.FailedLoginCount = 0;
                    await audit.WriteAsync(new AuditActor(user.Id, user.Username, ip), "AccountLocked", "User", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                await db.SaveChangesAsync(ct);
            }

            await audit.WriteAsync(new AuditActor(user?.Id, username, ip), "LoginFailed", details: user is null ? "Unknown user" : (!user.IsEnabled ? "User disabled" : "Wrong password"));
            return Results.Json(new { error = "Invalid user name or password." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        user.FailedLoginCount = 0;
        user.LockedUntilUtc = null;
        user.LastLoginUtc = now;
        if (needsRehash)
        {
            user.PasswordHash = passwords.Hash(user, password);
        }

        await db.SaveChangesAsync(ct);

        var token = await sessions.CreateAsync(user, ip, http.Request.Headers.UserAgent.ToString(), ct);
        http.Response.Cookies.Append(SessionAuthenticationHandler.CookieName, token, SessionAuthenticationHandler.CookieOptionsFor(http.Request));
        await audit.WriteAsync(new AuditActor(user.Id, user.Username, ip), "Login");

        return Results.Ok(new CurrentUserDto(user.Id, user.Username, user.DisplayName, user.Role.ToString(), user.MustChangePassword, security.SessionIdleTimeoutMin));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, SessionService sessions, AuditWriter audit, CancellationToken ct)
    {
        if (http.Request.Cookies.TryGetValue(SessionAuthenticationHandler.CookieName, out var token) && !string.IsNullOrEmpty(token))
        {
            await sessions.RevokeAsync(token, ct);
            if (http.User.Identity?.IsAuthenticated == true)
            {
                await audit.WriteAsync(http.Actor(), "Logout");
            }
        }

        http.Response.Cookies.Delete(SessionAuthenticationHandler.CookieName, SessionAuthenticationHandler.CookieOptionsFor(http.Request));
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(HttpContext http, NetWatchDbContext db, SettingsService settings, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == http.User.UserId(), ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(new CurrentUserDto(user.Id, user.Username, user.DisplayName, user.Role.ToString(), user.MustChangePassword, settings.Security.SessionIdleTimeoutMin));
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext http,
        NetWatchDbContext db,
        PasswordService passwords,
        SessionService sessions,
        SettingsService settings,
        AuditWriter audit,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == http.User.UserId(), ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (user.AuthSource != Core.AuthSource.Local)
        {
            return Results.Json(new { error = "Directory accounts change their password in Active Directory." }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!passwords.Verify(user, request.CurrentPassword ?? string.Empty).Valid)
        {
            await audit.WriteAsync(http.Actor(), "PasswordChangeFailed");
            return Results.Json(new { error = "Current password is incorrect." }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (PasswordService.Validate(request.NewPassword, user.Username, settings.Security.MinPasswordLength) is { } error)
        {
            return Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (passwords.Verify(user, request.NewPassword!).Valid)
        {
            return Results.Json(new { error = "New password must differ from the current one." }, statusCode: StatusCodes.Status400BadRequest);
        }

        user.PasswordHash = passwords.Hash(user, request.NewPassword!);
        user.MustChangePassword = false;
        user.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Other sessions of this user are signed out; the current one stays.
        await sessions.RevokeUserAsync(user.Id, http.User.SessionId(), ct);
        sessions.InvalidateUser(user.Id);
        await audit.WriteAsync(http.Actor(), "PasswordChanged", "User", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Results.NoContent();
    }
}
