using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using NetWatch.Core.Abstractions;
using NetWatch.Web.Api;
using NetWatch.Web.Auth;
using NetWatch.Web.Discovery;
using NetWatch.Web.Hubs;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web;

public static class WebServiceCollectionExtensions
{
    public static IServiceCollection AddNetWatchWeb(this IServiceCollection services)
    {
        services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        });

        services.AddSingleton<PasswordService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<LdapAuthenticator>();
        services.AddSingleton<DiscoveryService>();
        services.AddSingleton<ILiveUpdatePublisher, SignalRLivePublisher>();
        services.AddHostedService<SummaryBroadcaster>();
        services.AddSingleton<SelfMonitorService>();
        services.AddHostedService(sp => sp.GetRequiredService<SelfMonitorService>());
        services.AddSingleton<Reports.ReportService>();
        services.AddHostedService<Reports.ReportScheduler>();

        services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Viewer, p => p.RequireAuthenticatedUser().RequireRole(Policies.ViewerRoles))
            .AddPolicy(Policies.Operator, p => p.RequireAuthenticatedUser().RequireRole(Policies.OperatorRoles))
            .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireRole(nameof(Core.UserRole.Admin)));

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimits.Login, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(RateLimits.Heavy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        services.AddSignalR(o =>
        {
            o.EnableDetailedErrors = false;
            o.MaximumReceiveMessageSize = 32 * 1024;
        }).AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddProblemDetails();
        return services;
    }

    public static WebApplication UseNetWatchWeb(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseMiddleware<SecurityMiddleware>();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapAuthEndpoints();
        app.MapKioskEndpoints();
        app.MapDeviceEndpoints();
        app.MapInventoryEndpoints();
        app.MapAlertEndpoints();
        app.MapAdminEndpoints();
        app.MapOverviewEndpoints();
        app.MapDiscoveryEndpoints();
        app.MapServerEndpoints();
        app.MapEventEndpoints();
        app.MapMaintenanceEndpoints();
        app.MapVmwareEndpoints();
        app.MapDcEndpoints();
        Topology.TopologyEndpoints.MapTopologyEndpoints(app);
        app.MapBackupEndpoints();
        app.MapReportEndpoints();
        app.MapHub<LiveHub>("/hubs/live");

        app.UseEmbeddedUi();
        return app;
    }
}
