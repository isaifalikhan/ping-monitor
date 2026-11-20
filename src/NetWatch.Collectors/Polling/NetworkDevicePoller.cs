using System.Net;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Icmp;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Snmp;
using NetWatch.Core;

namespace NetWatch.Collectors.Polling;

/// <summary>
/// Polls a real device: ICMP availability in parallel with SNMP (uptime, interfaces, health, inventory) and,
/// for servers, WinRM/WMI or SSH collection. Each section fails independently, so a missing vendor MIB or a
/// blocked WinRM port never hides the other data.
/// </summary>
public sealed class NetworkDevicePoller : IDevicePoller
{
    private readonly IServerCollector _servers;
    private readonly TimeProvider _time;
    private readonly ILogger<NetworkDevicePoller> _logger;

    public NetworkDevicePoller(IServerCollector servers, TimeProvider time, ILogger<NetworkDevicePoller> logger)
    {
        _servers = servers;
        _time = time;
        _logger = logger;
    }

    public async Task<DevicePollResult> PollAsync(PollRequest request, CancellationToken ct)
    {
        var result = new DevicePollResult { DeviceId = request.Device.Id, TimestampUtc = _time.GetUtcNow().UtcDateTime };
        if (!IPAddress.TryParse(request.Device.IpAddress, out var ip))
        {
            result.LossPct = 100;
            return result;
        }

        var ping = PingProbe.ProbeAsync(ip, request.PingCount, request.PingTimeoutMs, ct);
        var snmp = request.Snmp is null ? Task.CompletedTask : PollSnmpAsync(ip, request, result, ct);
        var server = request.Server is null ? Task.FromResult<ServerPollData?>(null) : CollectServerAsync(request.Server, ct);
        await Task.WhenAll(ping, snmp, server);

        var p = await ping;
        result.PingOk = p.Reachable;
        result.RttMs = p.AvgRttMs;
        result.LossPct = p.LossPct;
        if (await server is { } s)
        {
            result.Server = s;
        }

        return result;
    }

    private async Task<ServerPollData?> CollectServerAsync(ServerPollRequest request, CancellationToken ct)
    {
        try
        {
            return await _servers.CollectAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Server collection failed for {Host}", request.Host);
            return new ServerPollData { Error = ex.Message };
        }
    }

    private async Task PollSnmpAsync(IPAddress ip, PollRequest request, DevicePollResult result, CancellationToken ct)
    {
        var client = new SnmpClient(ip, request.Snmp!, request.SnmpTimeoutMs, request.SnmpRetries);
        string? vendor;
        try
        {
            var sys = await client.GetAsync([Oids.SysUpTime, Oids.SysObjectId], ct);
            if (!sys.ContainsKey(Oids.SysUpTime))
            {
                throw new SnmpException("sysUpTime not returned");
            }

            result.SnmpOk = true;
            result.UptimeSec = (long?)(SnmpValues.UInt64(sys[Oids.SysUpTime]) / 100);
            vendor = VendorCatalog.VendorFromSysObjectId(SnmpValues.Text(sys.GetValueOrDefault(Oids.SysObjectId)));
        }
        catch (Exception ex) when (ex is SnmpException or TimeoutException)
        {
            result.SnmpOk = false;
            result.SnmpError = ex.Message;
            return;
        }

        if (request.RefreshInventory)
        {
            await Section("inventory", async () => result.Inventory = await SystemInfoProbe.ProbeAsync(client, ct));
        }

        if (request.RefreshInterfaces)
        {
            await Section("interface table", async () => result.InterfaceMetadata = await InterfaceCollector.ReadMetadataAsync(client, ct));
        }

        await Section("interface counters", async () => result.Interfaces.AddRange(await InterfaceCollector.ReadCountersAsync(client, ct)));
        await Section("health", async () =>
        {
            var health = await HealthCollector.ReadAsync(client, vendor, ct);
            result.CpuPct = health.CpuPct;
            result.MemPct = health.MemPct;
            result.Sensors.AddRange(health.Sensors);
        });

        if (request.SnmpStorage)
        {
            await Section("storage", async () =>
            {
                var server = new ServerPollData { Method = ServerCollectionMethod.Snmp, Ok = true };
                server.Volumes.AddRange(await SnmpStorageCollector.ReadAsync(client, ct));
                result.Server = server;
            });
        }

        async Task Section(string name, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is SnmpException or TimeoutException)
            {
                _logger.LogDebug(ex, "SNMP {Section} failed for {Device} ({Ip})", name, request.Device.Name, ip);
            }
        }
    }
}
