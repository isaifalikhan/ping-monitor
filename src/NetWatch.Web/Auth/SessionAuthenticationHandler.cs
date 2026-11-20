using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetWatch.Core;

namespace NetWatch.Web.Auth;

/// <summary>Authenticates requests from the session cookie (also used by the SignalR WebSocket).</summary>
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "NetWatchSession";
    public const string CookieName = "nw_session";
    public const string SessionIdClaim = "nw:sid";
    public const string MustChangePasswordClaim = "nw:mcp";

    private readonly SessionService _sessions;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        SessionService sessions)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token) || token.Length > 100)
        {
            return AuthenticateResult.NoResult();
        }

        var session = await _sessions.ValidateAsync(token, Context.RequestAborted);
        if (session is null)
        {
            Response.Cookies.Delete(CookieName, CookieOptionsFor(Request));
            return AuthenticateResult.Fail("Session expired");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, session.Username),
                new Claim(ClaimTypes.GivenName, session.DisplayName),
                new Claim(ClaimTypes.Role, session.Role.ToString()),
                new Claim(SessionIdClaim, session.SessionId),
                new Claim(MustChangePasswordClaim, session.MustChangePassword ? "1" : "0"),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public static CookieOptions CookieOptionsFor(HttpRequest request) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}

/// <summary>Authorisation policy names. Roles are hierarchical: Admin ⊃ Operator ⊃ Viewer.</summary>
public static class Policies
{
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string Admin = "admin";

    public static readonly string[] OperatorRoles = [nameof(UserRole.Admin), nameof(UserRole.Operator)];
    public static readonly string[] ViewerRoles = [nameof(UserRole.Admin), nameof(UserRole.Operator), nameof(UserRole.Viewer)];
}

public static class ClaimsPrincipalExtensions
{
    public static int UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    public static string Username(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Name) ?? "unknown";

    public static string? SessionId(this ClaimsPrincipal user) => user.FindFirstValue(SessionAuthenticationHandler.SessionIdClaim);

    public static bool MustChangePassword(this ClaimsPrincipal user) =>
        user.FindFirstValue(SessionAuthenticationHandler.MustChangePasswordClaim) == "1";
}
