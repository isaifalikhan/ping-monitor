using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;

namespace NetWatch.Collectors.Processing;

/// <summary>
/// Device names for the IP addresses in the MAC/ARP tables, looked up in the network's own DNS (reverse/PTR, as
/// "nslookup &lt;ip&gt;" does; on a domain this is the domain controller). Names are kept for 6 hours, failed lookups
/// are retried after 1 hour, and at most 4 lookups run at a time. IPs learned from simulated switches are not looked up.
/// </summary>
public sealed class HostNameCache : BackgroundService
{
    public static readonly TimeSpan NameTtl = TimeSpan.FromHours(6);
    public static readonly TimeSpan FailedRetry = TimeSpan.FromHours(1);

    private readonly MacTableStore _macs;
    private readonly InventoryCache? _inventory;
    private readonly ILogger<HostNameCache> _logger;
    private readonly Func<IPAddress, CancellationToken, Task<string?>> _lookup;
    private readonly ConcurrentDictionary<string, (string? Name, DateTime Utc)> _names = new(StringComparer.Ordinal);

    public HostNameCache(MacTableStore macs, InventoryCache inventory, ILogger<HostNameCache> logger)
        : this(macs, inventory, logger, ReverseDnsAsync)
    {
    }

    internal HostNameCache(MacTableStore macs, InventoryCache? inventory, ILogger<HostNameCache> logger, Func<IPAddress, CancellationToken, Task<string?>> lookup)
    {
        _macs = macs;
        _inventory = inventory;
        _logger = logger;
        _lookup = lookup;
    }

    /// <summary>The DNS name of <paramref name="ip"/> if it has been looked up and has one.</summary>
    public string? NameOf(string? ip) => ip is not null && _names.TryGetValue(ip, out var v) ? v.Name : null;

    private static async Task<string?> ReverseDnsAsync(IPAddress ip, CancellationToken ct)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), ct);
            return entry.HostName;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Looks up the IPs that have no fresh answer. Returns how many were looked up.</summary>
    internal async Task<int> ResolvePendingAsync(DateTime nowUtc, CancellationToken ct)
    {
        var due = _macs.KnownIps()
            .Where(x => _inventory?.GetSnapshot(x.SourceDeviceId)?.IsSimulated != true)
            .Select(x => x.Ip)
            .Distinct()
            .Where(ip => !_names.TryGetValue(ip, out var v) || nowUtc - v.Utc > (v.Name is null ? FailedRetry : NameTtl))
            .Take(2000)
            .ToList();
        using var gate = new SemaphoreSlim(4);
        var tasks = due.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                string? name = null;
                if (IPAddress.TryParse(ip, out var address))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        name = Clean(await _lookup(address, timeout.Token), ip);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                    }
                }

                _names[ip] = (name, nowUtc);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return due.Count;
    }

    /// <summary>Lower-case name without a trailing dot; null when the "name" is just the IP again.</summary>
    internal static string? Clean(string? name, string ip)
    {
        var n = name?.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(n) || n == ip || IPAddress.TryParse(n, out _) ? null : n.ToLowerInvariant();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var n = await ResolvePendingAsync(DateTime.UtcNow, stoppingToken);
                    if (n > 0)
                    {
                        _logger.LogDebug("Looked up {Count} host names for MAC table IPs", n);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Host name lookup for MAC table IPs failed");
                }

                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
