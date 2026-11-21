using Microsoft.Extensions.DependencyInjection;
using NetWatch.Alerts.Engine;
using NetWatch.Alerts.Notifications;
using NetWatch.Core.Alerts;

namespace NetWatch.Alerts;

public static class AlertsServiceCollectionExtensions
{
    public static IServiceCollection AddNetWatchAlerts(this IServiceCollection services)
    {
        services.AddSingleton<NotificationDispatcher>();
        services.AddSingleton<MailSender>();
        services.AddSingleton<INotificationSink>(sp => sp.GetRequiredService<NotificationDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatcher>());

        services.AddSingleton<AlertManager>();
        services.AddSingleton<IAlertSignalSink>(sp => sp.GetRequiredService<AlertManager>());
        services.AddSingleton<IActiveAlertIndex>(sp => sp.GetRequiredService<AlertManager>());
        services.AddHostedService(sp => sp.GetRequiredService<AlertManager>());
        services.AddScoped<AlertService>();
        return services;
    }
}
