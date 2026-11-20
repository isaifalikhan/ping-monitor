using Microsoft.AspNetCore.Http;
using NetWatch.Web.Auth;

namespace NetWatch.Web.Infrastructure;

/// <summary>
/// Request hardening:
/// <list type="bullet">
/// <item>Security headers (CSP allowing only same-origin resources: everything is bundled, nothing is external).</item>
/// <item>CSRF: state-changing API calls must carry the custom header "X-NetWatch-Request", which browsers will not
/// send cross-site without a CORS pre-flight (and CORS is not enabled). Combined with SameSite=Strict cookies.</item>
/// <item>Users flagged "must change password" can only call the auth endpoints.</item>
/// </list>
/// </summary>
public sealed class SecurityMiddleware
{
    public const string CsrfHeader = "X-NetWatch-Request";

    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "font-src 'self' data:; connect-src 'self'; media-src 'self' data:; object-src 'none'; frame-ancestors 'none'; " +
        "base-uri 'self'; form-action 'self'";

    private readonly RequestDelegate _next;

    public SecurityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Strict-Transport-Security"] = "max-age=31536000";

        var path = context.Request.Path;
        if (path.StartsWithSegments("/api"))
        {
            headers.CacheControl = "no-store";

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
                !HttpMethods.IsOptions(context.Request.Method) && !context.Request.Headers.ContainsKey(CsrfHeader))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Missing request header." });
                return;
            }

            if (context.User.Identity?.IsAuthenticated == true && context.User.MustChangePassword() &&
                !path.StartsWithSegments("/api/auth"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "You must change your password first.", code = "password_change_required" });
                return;
            }
        }

        await _next(context);
    }
}
