using Microsoft.Extensions.DependencyInjection;
using NetWatch.Alerts.Engine;
using NetWatch.Core.Alerts;

namespace NetWatch.Alerts;

public static class AlertsServiceCollectionExtensions
{
    public static IServiceCollection AddNetWatchAlerts(this IServiceCollection services)
    {
        services.AddSingleton<AlertManager>();
        services.AddSingleton<IAlertSignalSink>(sp => sp.GetRequiredService<AlertManager>());
        services.AddSingleton<IActiveAlertIndex>(sp => sp.GetRequiredService<AlertManager>());
        services.AddHostedService(sp => sp.GetRequiredService<AlertManager>());
        services.AddScoped<AlertService>();
        return services;
    }
}
