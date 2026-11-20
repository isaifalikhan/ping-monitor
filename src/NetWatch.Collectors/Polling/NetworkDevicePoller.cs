using System.Net;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Icmp;
using NetWatch.Collectors.Snmp;

namespace NetWatch.Collectors.Polling;

/// <summary>
/// Polls a real device: ICMP availability in parallel with SNMP (uptime, interfaces, health, inventory).
/// Each SNMP section fails independently, so a missing vendor MIB never hides interface data.
/// </summary>
public sealed class NetworkDevicePoller : IDevicePoller
{
    private readonly TimeProvider _time;
    private readonly ILogger<NetworkDevicePoller> _logger;

    public NetworkDevicePoller(TimeProvider time, ILogger<NetworkDevicePoller> logger)
    {
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
        await Task.WhenAll(ping, snmp);

        var p = await ping;
        result.PingOk = p.Reachable;
        result.RttMs = p.AvgRttMs;
        result.LossPct = p.LossPct;
        return result;
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
