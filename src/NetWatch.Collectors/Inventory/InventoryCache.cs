using System.Collections.Frozen;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Data.Config;

namespace NetWatch.Collectors.Inventory;

/// <summary>Immutable view of a device for the scheduler, pollers and alert engine.</summary>
public sealed record DeviceSnapshot(
    int Id,
    string Name,
    string IpAddress,
    DeviceType Type,
    int PollIntervalSec,
    bool IsEnabled,
    bool InMaintenance,
    int? SnmpCredentialId,
    int? GroupId,
    IReadOnlySet<string> Tags,
    IReadOnlySet<int> ImportantInterfaceIds,
    DeviceContext Context)
{
    public const string SimulatedTag = "simulated";

    public bool IsSimulated => Tags.Contains(SimulatedTag);
}

/// <summary>
/// In-memory copy of the inventory (devices, tags, groups, credentials, important ports). Reloaded every
/// minute and immediately after the API reports a change. Implements the device lookup used by the alert engine.
/// </summary>
public sealed class InventoryCache : BackgroundService, IDeviceContextProvider, IInventoryChangeNotifier
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<InventoryCache> _logger;
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile FrozenDictionary<int, DeviceSnapshot> _devices = FrozenDictionary<int, DeviceSnapshot>.Empty;

    public InventoryCache(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<InventoryCache> logger)
    {
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised after each reload (used to drop cached credentials).</summary>
    public event Action? Reloaded;

    /// <summary>Completes after the first successful load.</summary>
    public Task Ready => _ready.Task;

    public IReadOnlyCollection<DeviceSnapshot> All => _devices.Values;

    public DeviceSnapshot? GetSnapshot(int deviceId) => _devices.GetValueOrDefault(deviceId);

    public DeviceContext? Get(int deviceId) => _devices.GetValueOrDefault(deviceId)?.Context;

    public void NotifyInventoryChanged() => _changes.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReloadAsync(stoppingToken);
                _ready.TrySetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to load inventory");
            }

            // Wait for a change notification or the periodic refresh, whichever comes first.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(RefreshInterval);
            try
            {
                await _changes.Reader.ReadAsync(timeout.Token);

                // Coalesce bursts of changes (e.g. CSV import) into one reload.
                await Task.Delay(TimeSpan.FromMilliseconds(300), stoppingToken);
                while (_changes.Reader.TryRead(out _))
                {
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // periodic refresh
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Loads the inventory from the database. Public so start-up code and tests can force a load.</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var now = _time.GetUtcNow().UtcDateTime;

        var groups = await db.Groups.AsNoTracking().Select(g => new { g.Id, g.ParentId }).ToDictionaryAsync(g => g.Id, g => g.ParentId, ct);
        var devices = await db.Devices.AsNoTracking()
            .Include(d => d.Tags)
            .Include(d => d.Credentials)
            .ToListAsync(ct);
        var important = await db.Interfaces.AsNoTracking()
            .Where(i => i.IsImportant)
            .Select(i => new { i.DeviceId, i.Id })
            .ToListAsync(ct);
        var importantByDevice = important.GroupBy(i => i.DeviceId).ToDictionary(g => g.Key, g => (IReadOnlySet<int>)g.Select(i => i.Id).ToFrozenSet());

        var result = new Dictionary<int, DeviceSnapshot>(devices.Count);
        foreach (var d in devices)
        {
            var tags = d.Tags.Select(t => t.Tag).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            var ancestry = Ancestry(d.GroupId, groups);
            var inMaintenance = d.MaintenanceMode && (d.MaintenanceUntilUtc is null || d.MaintenanceUntilUtc > now);
            var context = new DeviceContext(d.Id, d.Name, d.IpAddress, d.Type, ancestry, tags, inMaintenance);
            result[d.Id] = new DeviceSnapshot(
                d.Id,
                d.Name,
                d.IpAddress,
                d.Type,
                Math.Max(15, d.PollIntervalSec),
                d.IsEnabled,
                inMaintenance,
                d.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.Snmp)?.CredentialProfileId,
                d.GroupId,
                tags,
                importantByDevice.GetValueOrDefault(d.Id) ?? FrozenSet<int>.Empty,
                context);
        }

        _devices = result.ToFrozenDictionary();
        Reloaded?.Invoke();
    }

    private static IReadOnlySet<int> Ancestry(int? groupId, IReadOnlyDictionary<int, int?> parents)
    {
        var set = new HashSet<int>();
        var current = groupId;

        // The depth guard protects against a corrupt (cyclic) tree.
        while (current is { } id && set.Add(id) && set.Count < 64)
        {
            current = parents.GetValueOrDefault(id);
        }

        return set.ToFrozenSet();
    }
}
