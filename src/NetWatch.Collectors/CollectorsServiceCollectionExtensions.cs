using Microsoft.Extensions.DependencyInjection;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Live;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Processing;
using NetWatch.Collectors.Scheduling;
using NetWatch.Collectors.Simulator;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;

namespace NetWatch.Collectors;

public static class CollectorsServiceCollectionExtensions
{
    public static IServiceCollection AddNetWatchCollectors(this IServiceCollection services)
    {
        services.AddSingleton<InventoryCache>();
        services.AddSingleton<IDeviceContextProvider>(sp => sp.GetRequiredService<InventoryCache>());
        services.AddSingleton<IInventoryChangeNotifier>(sp => sp.GetRequiredService<InventoryCache>());
        services.AddHostedService(sp => sp.GetRequiredService<InventoryCache>());

        services.AddSingleton<LiveStateCache>();
        services.AddSingleton<CollectorStats>();
        services.AddSingleton<CredentialResolver>();
        services.AddSingleton<PollResultProcessor>();
        services.AddSingleton<NetworkDevicePoller>();
        services.AddSingleton<SimulatedDevicePoller>();
        services.AddSingleton<IDevicePoller, RoutingDevicePoller>();

        services.AddSingleton<PollScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<PollScheduler>());
        services.AddHostedService<CounterStatePersister>();
        return services;
    }
}
