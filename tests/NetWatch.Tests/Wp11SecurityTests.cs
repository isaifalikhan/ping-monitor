using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetWatch.Alerts;
using NetWatch.Collectors;
using NetWatch.Core.Configuration;
using NetWatch.Data;
using NetWatch.Web;
using NetWatch.Web.Auth;
using NetWatch.Web.Diagnostics;

namespace NetWatch.Tests;

/// <summary>
/// WP11 security review, enforced: every API endpoint of the real application is authorized; only sign-in,
/// sign-out, the login options and kiosk sign-in are anonymous; Viewers cannot change anything (except their own
/// password and saved searches); administration areas require Admin.
/// </summary>
public sealed class Wp11SecurityTests : IDisposable
{
    private static readonly HashSet<string> Anonymous = ["POST /api/auth/login", "POST /api/auth/logout", "GET /api/auth/options", "POST /api/auth/kiosk"];

    /// <summary>Changes a Viewer may make: their own password and their own saved searches.</summary>
    private static readonly HashSet<string> ViewerWrites = ["POST /api/auth/change-password", "POST /api/saved-searches/", "DELETE /api/saved-searches/{id:int}"];

    private static readonly string[] AdminAreas =
        ["/api/users", "/api/credentials", "/api/settings", "/api/backups", "/api/diagnostics", "/api/kiosks", "/api/audit", "/api/discovery", "/api/devices/import"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-wp11-" + Guid.NewGuid().ToString("N"));

    private List<(string Key, string Pattern, AuthorizeAttributeLike Auth, bool Anonymous)> Endpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNetWatchData(new NetWatchPaths(_root));
        builder.Services.AddNetWatchAlerts();
        builder.Services.AddNetWatchCollectors(false);
        builder.Services.AddNetWatchWeb();
        builder.Services.AddSingleton(new ServerCertificateInfo(null, null, null));
        var app = builder.Build();
        app.UseNetWatchWeb();
        var list = new List<(string, string, AuthorizeAttributeLike, bool)>();
        foreach (var e in ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>())
        {
            var pattern = "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            if (!pattern.StartsWith("/api", StringComparison.Ordinal))
            {
                continue; // UI shell and static files carry no data
            }

            var methods = e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["ANY"];
            var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).Where(p => p is not null).Select(p => p!).ToList();
            var anonymous = e.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            foreach (var m in methods)
            {
                list.Add(($"{m} {pattern}", pattern, new AuthorizeAttributeLike(policies, e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count), anonymous));
            }
        }

        return list;
    }

    public sealed record AuthorizeAttributeLike(IReadOnlyList<string> Policies, int Count)
    {
        public bool Requires(string policy) => Policies.Contains(policy);

        public bool AtLeastOperator => Requires(NetWatch.Web.Auth.Policies.Operator) || Requires(NetWatch.Web.Auth.Policies.Admin);
    }

    [Fact]
    public void Every_api_endpoint_is_authorized_except_the_sign_in_endpoints()
    {
        var endpoints = Endpoints();
        Assert.True(endpoints.Count > 120, $"only {endpoints.Count} endpoints found");
        var open = endpoints.Where(e => e.Anonymous || e.Auth.Count == 0).Select(e => e.Key).Order().ToList();
        Assert.Equal(Anonymous.Order(), open);
    }

    [Fact]
    public void Viewers_cannot_change_anything()
    {
        var writes = Endpoints().Where(e => !e.Key.StartsWith("GET ", StringComparison.Ordinal) && !e.Anonymous).ToList();
        var viewerWritable = writes.Where(e => !e.Auth.AtLeastOperator).Select(e => e.Key).Where(k => !ViewerWrites.Contains(k))
            .Where(k => !k.Contains("/preview", StringComparison.Ordinal) && !k.EndsWith("/api/reports/run", StringComparison.Ordinal) && !k.EndsWith("/security-events/export", StringComparison.Ordinal))
            .Order().ToList();
        Assert.Empty(viewerWritable); // report generation/preview are reads (POST only because of the body)
    }

    [Fact]
    public void Administration_requires_admin()
    {
        var weak = Endpoints().Where(e => AdminAreas.Any(a => e.Pattern.StartsWith(a, StringComparison.Ordinal)) && !e.Auth.Requires(Policies.Admin))
            .Select(e => e.Key).Order().ToList();
        Assert.Empty(weak);
    }

    [Fact]
    public void Session_cookie_is_http_only_secure_and_strict()
    {
        var o = SessionAuthenticationHandler.CookieOptionsFor(new Microsoft.AspNetCore.Http.DefaultHttpContext().Request);
        Assert.True(o.HttpOnly);
        Assert.True(o.Secure);
        Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Strict, o.SameSite);
        Assert.Null(o.Expires); // session cookie; only kiosk displays get a persistent one
    }

    [Fact]
    public void Diagnostics_never_export_secret_named_settings()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse("{\"smtp\":{\"smtpPasswordProtected\":\"x\"},\"snmp\":{\"community\":\"y\"}}");
        Assert.DoesNotContain("\"x\"", DiagnosticBundleService.Redact(node)!.ToJsonString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
