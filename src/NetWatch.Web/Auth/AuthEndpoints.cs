using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Core;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Auth;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record CurrentUserDto(int Id, string Username, string DisplayName, string Role, bool MustChangePassword, int SessionIdleTimeoutMin, string AuthSource);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");
        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Login);
        group.MapPost("/logout", LogoutAsync).AllowAnonymous();
        group.MapGet("/me", MeAsync).RequireAuthorization(Policies.Viewer);
        group.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization(Policies.Viewer);
        group.MapGet("/options", (SettingsService s) => Results.Ok(new { ldapEnabled = s.Ldap.Enabled, domain = s.Ldap.Enabled ? s.Ldap.Domain : null })).AllowAnonymous();
    }

    private static IResult Error(string message, int status) => Results.Json(new { error = message }, statusCode: status);

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        NetWatchDbContext db,
        PasswordService passwords,
        SessionService sessions,
        SettingsService settings,
        LdapAuthenticator ldap,
        AuditWriter audit,
        CancellationToken ct)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        var ip = http.Connection.RemoteIpAddress?.ToString();
        if (username.Length == 0 || password.Length == 0 || username.Length > 128 || password.Length > 256)
        {
            return Error("Enter user name and password.", StatusCodes.Status400BadRequest);
        }

        var security = settings.Security;
        var directory = settings.Ldap;
        var now = DateTime.UtcNow;

        // Local accounts match the name as typed; directory accounts are stored under their sAMAccountName.
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        var sam = directory.Enabled ? LdapAuthenticator.NormalizeUsername(username) : null;
        if (user is null && sam is not null)
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Username == sam && u.AuthSource == AuthSource.Ldap, ct);
        }

        if (user is { LockedUntilUtc: { } lockedUntil } && lockedUntil > now)
        {
            passwords.Verify(null, password);
            await audit.WriteAsync(new AuditActor(user.Id, user.Username, ip), "LoginBlockedLocked");
            return Error($"Account is locked after too many failed attempts. Try again after {(int)Math.Ceiling((lockedUntil - now).TotalMinutes)} minute(s) or ask an administrator to unlock it.", StatusCodes.Status423Locked);
        }

        var useDirectory = directory.Enabled && (user is null ? sam is not null : user.AuthSource == AuthSource.Ldap);
        if (useDirectory)
        {
            var result = await ldap.AuthenticateAsync(directory, username, password, ct);
            if (!result.Success)
            {
                await RegisterFailureAsync(db, user, security, audit, username, ip, $"Directory: {result.Error}", ct);
                return result.DirectoryUnavailable
                    ? Error("The directory (domain controllers) cannot be reached. Try again later or sign in with a local account.", StatusCodes.Status503ServiceUnavailable)
                    : Error("Invalid user name or password.", StatusCodes.Status401Unauthorized);
            }

            if (result.Role is not { } role)
            {
                await audit.WriteAsync(new AuditActor(user?.Id, username, ip), "LoginDenied", details: result.Error);
                return Error("Your account is not in a directory group that has access to NetWatch.", StatusCodes.Status403Forbidden);
            }

            if (user is { IsEnabled: false })
            {
                await audit.WriteAsync(new AuditActor(user.Id, user.Username, ip), "LoginFailed", details: "User disabled in NetWatch");
                return Error("Invalid user name or password.", StatusCodes.Status401Unauthorized);
            }

            if (user is null)
            {
                user = new User { Username = result.SamAccountName ?? sam!, AuthSource = AuthSource.Ldap, IsEnabled = true, CreatedUtc = now };
                db.Users.Add(user);
            }

            // The role follows the AD groups at every sign-in.
            user.DisplayName = string.IsNullOrWhiteSpace(result.DisplayName) ? user.Username : result.DisplayName;
            user.Email = result.Email;
            user.Role = role;
            user.PasswordHash = null;
            user.MustChangePassword = false;
            user.UpdatedUtc = now;
            await db.SaveChangesAsync(ct);
            return await CompleteAsync(user, http, db, sessions, audit, security, ip, $"directory, groups: {string.Join("; ", result.MatchedGroups)}", ct);
        }

        var (valid, needsRehash) = passwords.Verify(user?.AuthSource == AuthSource.Local ? user : null, password);
        if (user is null || !valid || !user.IsEnabled)
        {
            await RegisterFailureAsync(db, user, security, audit, username, ip,
                user is null ? "Unknown user" : !user.IsEnabled ? "User disabled" : user.AuthSource == AuthSource.Ldap ? "Directory sign-in is disabled" : "Wrong password", ct);
            return Error("Invalid user name or password.", StatusCodes.Status401Unauthorized);
        }

        if (needsRehash)
        {
            user.PasswordHash = passwords.Hash(user, password);
        }

        return await CompleteAsync(user, http, db, sessions, audit, security, ip, null, ct);
    }

    private static async Task RegisterFailureAsync(NetWatchDbContext db, User? user, Core.Configuration.SecuritySettings security, AuditWriter audit, string username, string? ip, string reason, CancellationToken ct)
    {
        if (user is not null)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= security.MaxFailedLogins)
            {
                user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(security.LockoutMinutes);
                user.FailedLoginCount = 0;
                await audit.WriteAsync(new AuditActor(user.Id, user.Username, ip), "AccountLocked", "User", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            await db.SaveChangesAsync(ct);
        }

        await audit.WriteAsync(new AuditActor(user?.Id, username, ip), "LoginFailed", details: reason);
    }

    private static async Task<IResult> CompleteAsync(User user, HttpContext http, NetWatchDbContext db, SessionService sessions, AuditWriter audit, Core.Configuration.SecuritySettings security, string? ip, string? details, CancellationToken ct)
    {
        user.FailedLoginCount = 0;
        user.LockedUntilUtc = null;
        user.LastLoginUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // A role change from the directory must not keep stale cached claims.
        sessions.InvalidateUser(user.Id);
        var token = await sessions.CreateAsync(user, ip, http.Request.Headers.UserAgent.ToString(), ct);
        http.Response.Cookies.Append(SessionAuthenticationHandler.CookieName, token, SessionAuthenticationHandler.CookieOptionsFor(http.Request));
        await audit.WriteAsync(new AuditActor(user.Id, user.Username, ip), "Login", details: details);

        return Results.Ok(new CurrentUserDto(user.Id, user.Username, user.DisplayName, user.Role.ToString(), user.MustChangePassword, security.SessionIdleTimeoutMin, user.AuthSource.ToString()));
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
            : Results.Ok(new CurrentUserDto(user.Id, user.Username, user.DisplayName, user.Role.ToString(), user.MustChangePassword, settings.Security.SessionIdleTimeoutMin, user.AuthSource.ToString()));
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

        if (user.AuthSource != AuthSource.Local)
        {
            return Error("Directory accounts change their password in Active Directory.", StatusCodes.Status400BadRequest);
        }

        if (!passwords.Verify(user, request.CurrentPassword ?? string.Empty).Valid)
        {
            await audit.WriteAsync(http.Actor(), "PasswordChangeFailed");
            return Error("Current password is incorrect.", StatusCodes.Status400BadRequest);
        }

        if (PasswordService.Validate(request.NewPassword, user.Username, settings.Security.MinPasswordLength) is { } error)
        {
            return Error(error, StatusCodes.Status400BadRequest);
        }

        if (passwords.Verify(user, request.NewPassword!).Valid)
        {
            return Error("New password must differ from the current one.", StatusCodes.Status400BadRequest);
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
