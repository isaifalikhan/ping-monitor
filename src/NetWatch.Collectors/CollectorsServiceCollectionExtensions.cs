using Microsoft.Extensions.DependencyInjection;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Live;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Processing;
using NetWatch.Collectors.Receivers;
using NetWatch.Collectors.Scheduling;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Servers.Linux;
using NetWatch.Collectors.Servers.Windows;
using NetWatch.Collectors.Simulator;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;

namespace NetWatch.Collectors;

public static class CollectorsServiceCollectionExtensions
{
    /// <param name="simulate">Also generate syslog/trap traffic for the simulated devices.</param>
    public static IServiceCollection AddNetWatchCollectors(this IServiceCollection services, bool simulate = false)
    {
        services.AddSingleton<InventoryCache>();
        services.AddSingleton<IDeviceContextProvider>(sp => sp.GetRequiredService<InventoryCache>());
        services.AddSingleton<IInventoryChangeNotifier>(sp => sp.GetRequiredService<InventoryCache>());
        services.AddHostedService(sp => sp.GetRequiredService<InventoryCache>());

        services.AddSingleton<LiveStateCache>();
        services.AddSingleton<IDependencyStatus, DependencyStatus>();
        services.AddSingleton<CollectorStats>();
        services.AddSingleton<CredentialResolver>();

        // Servers (WinRM/WMI, event logs, SSH).
        services.AddSingleton<WindowsServerCollector>();
        services.AddSingleton<SshServerCollector>();
        services.AddSingleton<IServerCollector, ServerCollectorRouter>();
        services.AddSingleton<ServerResultProcessor>();

        // VMware (vSphere Web Services).
        services.AddSingleton<Vmware.VmwareCollector>();
        services.AddSingleton<Vmware.IVmwareCollector>(sp => sp.GetRequiredService<Vmware.VmwareCollector>());
        services.AddSingleton<VmwareResultProcessor>();

        // Domain controllers (Security log, replication, clock; optional Windows Event Forwarding).
        services.AddSingleton<DomainControllers.IDcCollector, DomainControllers.DcCollector>();
        services.AddSingleton<DcResultProcessor>();
        services.AddSingleton<DomainControllers.ForwardedEventsReader>();
        services.AddHostedService(sp => sp.GetRequiredService<DomainControllers.ForwardedEventsReader>());

        services.AddSingleton<PollResultProcessor>();
        services.AddSingleton<Snmp.SnmpHealthProfileStore>();
        services.AddSingleton<Hardware.IHardwareCollector, Hardware.HardwareCollector>();
        services.AddSingleton<Processing.HardwareResultProcessor>();
        services.AddSingleton<NetworkDevicePoller>();
        services.AddSingleton<SimulatedDevicePoller>();
        services.AddSingleton<IDevicePoller, RoutingDevicePoller>();

        services.AddSingleton<PollScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<PollScheduler>());
        services.AddHostedService<CounterStatePersister>();

        // Syslog and SNMP trap receivers.
        services.AddSingleton<ReceiverStats>();
        services.AddSingleton<EventIngestService>();
        services.AddHostedService(sp => sp.GetRequiredService<EventIngestService>());
        services.AddSingleton<SyslogPipeline>();
        services.AddSingleton<TrapPipeline>();
        services.AddHostedService<SyslogReceiver>();
        services.AddHostedService<TrapReceiver>();

        if (simulate)
        {
            services.AddHostedService<SimulatedEventGenerator>();
        }

        return services;
    }
}
