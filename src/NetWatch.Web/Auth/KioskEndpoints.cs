using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Core;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Auth;

public sealed record KioskDisplayDto(int Id, string Name, DateTime CreatedUtc, string? CreatedBy, DateTime? LastUsedUtc, bool Revoked);

public sealed record KioskCreateInput(string? Name);

public sealed record KioskSignInInput(string? Token);

/// <summary>
/// NOC wall displays without a person signed in: an administrator creates a named display and gets a one-time
/// link with a random token. Opening the link signs the browser in as a read-only (Viewer) kiosk account whose
/// session has no idle timeout. Kiosk accounts cannot sign in with a password; revoking a display ends its
/// sessions at once. Only the SHA-256 of the token is stored.
/// </summary>
public static class KioskEndpoints
{
    public static void MapKioskEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/kiosk", SignInAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Login);

        var admin = app.MapGroup("/api/kiosks").RequireAuthorization(Policies.Admin);
        admin.MapGet("/", async (NetWatchDbContext db, CancellationToken ct) =>
            Results.Ok(await db.KioskDisplays.AsNoTracking().OrderBy(k => k.Name)
                .Select(k => new KioskDisplayDto(k.Id, k.Name, k.CreatedUtc, k.CreatedBy, k.LastUsedUtc, k.RevokedUtc != null)).ToListAsync(ct)));
        admin.MapPost("/", CreateAsync);
        admin.MapDelete("/{id:int}", RevokeAsync);
    }

    private static async Task<IResult> CreateAsync(KioskCreateInput input, NetWatchDbContext db, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 60)
        {
            return Results.BadRequest(new { error = "Give the display a name of 1 to 60 characters (e.g. \"NOC wall left\")." });
        }

        if (await db.KioskDisplays.AnyAsync(k => k.Name == name && k.RevokedUtc == null, ct))
        {
            return Results.Conflict(new { error = "A display with this name already exists." });
        }

        var now = DateTime.UtcNow;
        var user = new User
        {
            Username = $"kiosk:{Guid.NewGuid():N}"[..20],
            DisplayName = $"NOC display {name}",
            AuthSource = AuthSource.Kiosk,
            Role = UserRole.Viewer,
            IsEnabled = true,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var display = new KioskDisplay { Name = name, TokenHash = SessionService.HashToken(token), UserId = user.Id, CreatedBy = http.Actor().Username, CreatedUtc = now };
        db.KioskDisplays.Add(display);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(http.Actor(), "KioskCreated", "Kiosk", display.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), name);

        // The token is shown once; only its hash is stored.
        return Results.Ok(new { id = display.Id, name, path = $"/noc?kiosk={token}" });
    }

    private static async Task<IResult> RevokeAsync(int id, NetWatchDbContext db, SessionService sessions, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        var display = await db.KioskDisplays.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (display is null)
        {
            return Results.NotFound();
        }

        display.RevokedUtc ??= DateTime.UtcNow;
        await db.Users.Where(u => u.Id == display.UserId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsEnabled, false), ct);
        await db.UserSessions.Where(s => s.UserId == display.UserId && s.RevokedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, DateTime.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        sessions.InvalidateUser(display.UserId);
        await audit.WriteAsync(http.Actor(), "KioskRevoked", "Kiosk", id.ToString(System.Globalization.CultureInfo.InvariantCulture), display.Name);
        return Results.NoContent();
    }

    private static async Task<IResult> SignInAsync(KioskSignInInput input, NetWatchDbContext db, SessionService sessions, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString();
        if (string.IsNullOrWhiteSpace(input.Token) || input.Token.Length > 100)
        {
            return Results.Json(new { error = "Invalid display link." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var hash = SessionService.HashToken(input.Token.Trim());
        var display = await db.KioskDisplays.FirstOrDefaultAsync(k => k.TokenHash == hash && k.RevokedUtc == null, ct);
        var user = display is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == display.UserId && u.IsEnabled && u.AuthSource == AuthSource.Kiosk, ct);
        if (display is null || user is null)
        {
            await audit.WriteAsync(new AuditActor(null, "kiosk", ip), "KioskSignInFailed");
            return Results.Json(new { error = "This display link is not valid any more. Ask an administrator for a new one." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        display.LastUsedUtc = DateTime.UtcNow;
        user.LastLoginUtc = display.LastUsedUtc;
        await db.SaveChangesAsync(ct);
        var token = await sessions.CreateAsync(user, ip, http.Request.Headers.UserAgent.ToString(), ct, kiosk: true);
        http.Response.Cookies.Append(SessionAuthenticationHandler.CookieName, token, SessionAuthenticationHandler.CookieOptionsFor(http.Request, persistent: true));
        await audit.WriteAsync(new AuditActor(user.Id, user.Username, ip), "KioskSignIn", "Kiosk", display.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), display.Name);
        return Results.Ok(new { name = display.Name });
    }
}
