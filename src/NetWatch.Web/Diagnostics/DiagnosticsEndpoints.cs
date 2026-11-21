using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Diagnostics;

/// <summary>Pilot tools (admin only): connection tests and the diagnostic bundle.</summary>
public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/diagnostics").RequireAuthorization(Policies.Admin);
        admin.MapPost("/test", TestAsync).RequireRateLimiting(RateLimits.Heavy);
        admin.MapGet("/tests", (ConnectionTestService tests) => Results.Ok(tests.History));
        admin.MapGet("/bundle", BundleAsync).RequireRateLimiting(RateLimits.Heavy);
    }

    private static async Task<IResult> TestAsync(ConnectionTestRequest request, ConnectionTestService tests, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        var result = await tests.RunAsync(request, http.Actor().Username, ct);
        await audit.WriteAsync(http.Actor(), "ConnectionTest", request.Kind.ToString(), result.Target, result.Ok ? "ok" : result.Error);
        return Results.Ok(result);
    }

    /// <param name="walk">Comma-separated device IDs to include an SNMP walk for (at most 20).</param>
    private static async Task<IResult> BundleAsync(string? walk, DiagnosticBundleService bundles, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        var ids = (walk ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1).Where(v => v > 0).Distinct().Take(20).ToList();
        var bytes = await bundles.BuildAsync(ids, ct);
        await audit.WriteAsync(http.Actor(), "DiagnosticBundle", "Diagnostics", null, $"{bytes.Length:N0} bytes, SNMP walks of {ids.Count} device(s)");
        return Results.File(bytes, "application/zip", $"netwatch-diagnostics-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
    }
}
