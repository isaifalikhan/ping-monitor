using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Processing;
using NetWatch.Collectors.Vmware;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Data.Events;

namespace NetWatch.Tests;

/// <summary>Regressions found by the 2x load test.</summary>
public sealed class Wp11RegressionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-wp11r-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(new NetWatchPaths(_root));
        _sp = services.BuildServiceProvider();
        await _sp.InitialiseDatabasesAsync(NullLogger.Instance);
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class NullSink : IAlertSignalSink
    {
        public void Publish(AlertSignal signal)
        {
        }
    }

    [Fact]
    public async Task Vcenter_events_are_stored_once()
    {
        // Before the fix the "already known" query put data.Events.Min(...) inside the EF expression and every
        // poll that returned events failed: no vCenter event was ever stored.
        var scopes = _sp.GetRequiredService<IServiceScopeFactory>();
        var processor = new VmwareResultProcessor(scopes, _sp.GetRequiredService<IMetricStore>(), new NullSink(), _sp.GetRequiredService<SettingsService>(), null!,
            new InventoryCache(scopes, TimeProvider.System, NullLogger<InventoryCache>.Instance), NullLogger<VmwareResultProcessor>.Instance);
        var tags = new HashSet<string>();
        var vc = new DeviceSnapshot(1, "VC-01", "10.0.0.50", DeviceType.VCenter, 60, true, false, null, null, tags, new HashSet<int>(),
            new DeviceContext(1, "VC-01", "10.0.0.50", DeviceType.VCenter, new HashSet<int>(), tags, false));

        async Task<int> RunAsync(params long[] keys)
        {
            var data = new VmwarePollData { Ok = true };
            data.Events.AddRange(keys.Select(k => new VmwareEventReading(k, DateTime.UtcNow, "HostConnectionLostEvent", Severity.Major, null, "esx01", $"event {k}")));
            var result = new DevicePollResult { DeviceId = 1, TimestampUtc = DateTime.UtcNow, Vmware = data };
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
            await processor.ProcessAsync(vc, result, db, events, new List<DeviceEvent>(), CancellationToken.None);
            await events.SaveChangesAsync();
            return await events.VmwareEvents.CountAsync();
        }

        Assert.Equal(3, await RunAsync(101, 102, 103));
        Assert.Equal(4, await RunAsync(102, 103, 104)); // overlap after a restart is not stored twice
    }
}
