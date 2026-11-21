using System.Collections.Frozen;
using System.Net;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;

namespace NetWatch.Collectors.Inventory;

/// <summary>Immutable view of a device for the scheduler, pollers, receivers and alert engine.</summary>
/// <param name="MaintenanceReason">"Maintenance mode" or the name of the active maintenance window; null when not in maintenance.</param>
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
    DeviceContext Context,
    OsKind Os = OsKind.Unknown,
    int? ManagementCredentialId = null,
    int? ParentDeviceId = null,
    IReadOnlyList<string>? WatchedServices = null,
    string? SshHostKeyFingerprint = null,
    string? MaintenanceReason = null,
    DateTime? MaintenanceUntilUtc = null,
    int? VcenterCredentialId = null,
    string? TlsThumbprint = null)
{
    public const string SimulatedTag = "simulated";

    public bool IsSimulated => Tags.Contains(SimulatedTag);

    /// <summary>Servers, VMs, DCs and hypervisors get server collection (disks, services ...).</summary>
    public bool IsServerType => Type is DeviceType.Server or DeviceType.VM or DeviceType.DomainController or DeviceType.Hypervisor;
}

/// <summary>
/// In-memory copy of the inventory (devices, tags, groups, credentials, important ports, watched services,
/// maintenance windows). Reloaded every 30 seconds (so maintenance windows start and end on time) and immediately
/// after the API reports a change. Implements the device lookup used by the alert engine.
/// </summary>
public sealed class InventoryCache : BackgroundService, IDeviceContextProvider, IInventoryChangeNotifier
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<InventoryCache> _logger;
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile FrozenDictionary<int, DeviceSnapshot> _devices = FrozenDictionary<int, DeviceSnapshot>.Empty;
    private volatile FrozenDictionary<string, DeviceSnapshot> _byIp = FrozenDictionary<string, DeviceSnapshot>.Empty;
    private volatile FrozenDictionary<string, DeviceSnapshot> _byName = FrozenDictionary<string, DeviceSnapshot>.Empty;
    private volatile IReadOnlyList<MaintenanceWindow> _windows = Array.Empty<MaintenanceWindow>();

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

    /// <summary>Maintenance windows as of the last reload.</summary>
    public IReadOnlyList<MaintenanceWindow> MaintenanceWindows => _windows;

    public DeviceSnapshot? GetSnapshot(int deviceId) => _devices.GetValueOrDefault(deviceId);

    public DeviceContext? Get(int deviceId) => _devices.GetValueOrDefault(deviceId)?.Context;

    /// <summary>Device with this management IP (used to map syslog/trap sources), or null.</summary>
    public DeviceSnapshot? FindByIp(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return _byIp.GetValueOrDefault(ip.ToString());
    }

    /// <summary>
    /// Finds the inventory device for a VMware VM or host: by IP address, then by name (also the short host name
    /// of an FQDN). Used to attach VMware alerts to the device operators already know.
    /// </summary>
    public DeviceSnapshot? FindByAddressOrName(string? ip, string? name)
    {
        if (!string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out var address) && FindByIp(address) is { } byIp)
        {
            return byIp;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (IPAddress.TryParse(name, out var nameIp) && FindByIp(nameIp) is { } byNameIp)
        {
            return byNameIp;
        }

        var shortName = name.Split('.')[0];
        return _byName.GetValueOrDefault(name) ?? _byName.GetValueOrDefault(shortName);
    }

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
            .Include(d => d.WatchedServices)
            .ToListAsync(ct);
        var important = await db.Interfaces.AsNoTracking()
            .Where(i => i.IsImportant)
            .Select(i => new { i.DeviceId, i.Id })
            .ToListAsync(ct);
        var importantByDevice = important.GroupBy(i => i.DeviceId).ToDictionary(g => g.Key, g => (IReadOnlySet<int>)g.Select(i => i.Id).ToFrozenSet());
        var windows = await db.MaintenanceWindows.AsNoTracking().Where(w => w.IsEnabled).ToListAsync(ct);
        var activeWindows = windows
            .Select(w => (Window: w, End: MaintenanceSchedule.CurrentEnd(w, now), Scope: RuleScope.Parse(w.ScopeJson)))
            .Where(x => x.End is not null)
            .ToList();

        var result = new Dictionary<int, DeviceSnapshot>(devices.Count);
        foreach (var d in devices)
        {
            var tags = d.Tags.Select(t => t.Tag).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            var ancestry = Ancestry(d.GroupId, groups);

            string? reason = null;
            DateTime? until = null;
            if (d.MaintenanceMode && (d.MaintenanceUntilUtc is null || d.MaintenanceUntilUtc > now))
            {
                reason = "Maintenance mode";
                until = d.MaintenanceUntilUtc;
            }
            else
            {
                var probe = new DeviceContext(d.Id, d.Name, d.IpAddress, d.Type, ancestry, tags, false);
                var match = activeWindows.FirstOrDefault(x => x.Scope.Matches(probe, interfaceIsImportant: false, isInterfaceSignal: false));
                if (match.Window is not null)
                {
                    reason = $"Maintenance window \"{match.Window.Name}\"";
                    until = match.End;
                }
            }

            var inMaintenance = reason is not null;
            var context = new DeviceContext(d.Id, d.Name, d.IpAddress, d.Type, ancestry, tags, inMaintenance, d.IsEnabled);
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
                context,
                d.Os,
                d.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.Management)?.CredentialProfileId,
                d.ParentDeviceId,
                d.WatchedServices.Select(s => s.ServiceName).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                d.SshHostKeyFingerprint,
                reason,
                until,
                d.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.VCenter)?.CredentialProfileId,
                d.TlsThumbprint);
        }

        _devices = result.ToFrozenDictionary();
        _byIp = result.Values
            .GroupBy(d => d.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _byName = result.Values
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _windows = windows;
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
