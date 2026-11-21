using System.Globalization;
using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Receivers;
using NetWatch.Core;

namespace NetWatch.Collectors.Simulator;

/// <summary>
/// Simulator mode only: produces syslog messages and SNMP traps "from" the simulated devices and feeds them
/// through the real receiver pipelines (parsing, trap decoding, device mapping, storage, alert rules). Traps are
/// encoded to bytes and decoded again, exactly as if they had arrived on UDP 162.
/// </summary>
public sealed class SimulatedEventGenerator : BackgroundService
{
    private readonly InventoryCache _inventory;
    private readonly SyslogPipeline _syslog;
    private readonly TrapPipeline _traps;
    private readonly ILogger<SimulatedEventGenerator> _logger;
    private readonly Random _rng = new(4242);

    public SimulatedEventGenerator(InventoryCache inventory, SyslogPipeline syslog, TrapPipeline traps, ILogger<SimulatedEventGenerator> logger)
    {
        _inventory = inventory;
        _syslog = syslog;
        _traps = traps;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _inventory.Ready.WaitAsync(stoppingToken);
        _logger.LogInformation("Simulator: generating syslog messages and SNMP traps for simulated devices");
        if (SimulatedDomainController.IncidentRate > 1)
        {
            _logger.LogWarning("Simulator: DC incident rate x{Rate} (NETWATCH_SIM_INCIDENT_RATE) - for demonstrations and tests only", SimulatedDomainController.IncidentRate);
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                tick++;
                var devices = _inventory.All.Where(d => d.IsSimulated && d.IsEnabled).ToList();
                if (devices.Count == 0)
                {
                    continue;
                }

                var switches = devices.Where(d => d.Type == DeviceType.Switch).ToList();
                var servers = devices.Where(d => d.Type == DeviceType.Server && d.Os == OsKind.Linux).ToList();

                // ~4 routine syslog messages every 5 seconds.
                for (var i = 0; i < 4; i++)
                {
                    var d = switches[_rng.Next(switches.Count)];
                    _syslog.Accept(IPAddress.Parse(d.IpAddress), SwitchSyslog(d, critical: _rng.NextDouble() < 0.002));
                }

                // Occasionally a Linux server sees a burst of failed SSH logins (triggers the count-in-window rule).
                if (servers.Count > 0 && _rng.NextDouble() < 0.004)
                {
                    var target = servers[_rng.Next(servers.Count)];
                    var attacker = $"10.99.{_rng.Next(1, 254)}.{_rng.Next(1, 254)}";
                    for (var i = 0; i < 14; i++)
                    {
                        _syslog.Accept(IPAddress.Parse(target.IpAddress),
                            $"<86>{Bsd(DateTime.UtcNow)} {target.Name} sshd[{_rng.Next(1000, 60000)}]: Failed password for invalid user admin from {attacker} port {_rng.Next(30000, 60000)} ssh2");
                    }
                }

                // A few traps per minute: link flaps on access ports, config changes, rare auth failures.
                if (tick % 3 == 0 && _rng.NextDouble() < 0.5)
                {
                    var d = switches[_rng.Next(switches.Count)];
                    var r = _rng.NextDouble();
                    var trap = r < 0.6 ? LinkTrap(_rng.Next(1, 49), down: _rng.NextDouble() < 0.5)
                        : r < 0.95 ? Trap("1.3.6.1.4.1.9.9.43.2.0.1")
                        : Trap("1.3.6.1.6.3.1.1.5.5");
                    var decoded = TrapDecoder.Decode(MessageFactory.ParseMessages(trap.ToBytes(), new UserRegistry())[0]);
                    if (decoded is not null)
                    {
                        await _traps.AcceptAsync(IPAddress.Parse(d.IpAddress), decoded, stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Bsd(DateTime utc) =>
        Core.Configuration.LocalTime.ToLocal(utc).ToString("MMM d HH:mm:ss", CultureInfo.InvariantCulture);

    private string SwitchSyslog(DeviceSnapshot d, bool critical)
    {
        var now = DateTime.UtcNow;
        var port = _rng.Next(1, 49);
        var vendor = d.Id % 3;
        if (critical)
        {
            return vendor switch
            {
                0 => $"<186>{_rng.Next(1000, 99999)}: {Bsd(now)}: %PLATFORM_ENV-2-FAN: Fan 1 had a rotation error reported.",
                1 => $"<10>{Bsd(now)} {d.Name} 00405 chassis: Power supply 2 failure detected.",
                _ => $"<10>1 {now:yyyy-MM-ddTHH:mm:ss.fffZ} {d.Name} chassisd - CHASSISD_FRU_OFFLINE_NOTICE - Taking FPC 0 offline: Error",
            };
        }

        var messages = vendor switch
        {
            0 => new[]
            {
                $"<189>{_rng.Next(1000, 99999)}: {Bsd(now)}: %LINEPROTO-5-UPDOWN: Line protocol on Interface GigabitEthernet1/0/{port}, changed state to {(_rng.Next(2) == 0 ? "down" : "up")}",
                $"<190>{_rng.Next(1000, 99999)}: {Bsd(now)}: %SYS-6-LOGGINGHOST_STARTSTOP: Logging to host 10.250.0.250 port 514 started - CLI initiated",
                $"<189>{_rng.Next(1000, 99999)}: {Bsd(now)}: %SYS-5-CONFIG_I: Configured from console by netadmin on vty0 (10.250.0.200)",
                $"<188>{_rng.Next(1000, 99999)}: {Bsd(now)}: %ILPOWER-4-LOG_OVERDRAWN: Interface Gi1/0/{port} is overdrawing power.",
            },
            1 => new[]
            {
                $"<14>{Bsd(now)} {d.Name} 00076 ports: port {port} is now {(_rng.Next(2) == 0 ? "off-line" : "on-line")}",
                $"<13>{Bsd(now)} {d.Name} 00179 mgr: SME SSH from 10.250.0.200 - MANAGER Mode",
                $"<12>{Bsd(now)} {d.Name} 00331 FFI: port {port}-Excessive CRC/alignment errors. See help.",
            },
            _ => new[]
            {
                $"<30>1 {now:yyyy-MM-ddTHH:mm:ss.fffZ} {d.Name} mib2d - SNMP_TRAP_LINK_DOWN [junos@2636.1.1.1.2.63 snmp-interface-index=\"{500 + port}\" interface-name=\"ge-0/0/{port}\"] ifIndex {500 + port}, ifAdminStatus up(1), ifOperStatus down(2), ifName ge-0/0/{port}",
                $"<29>1 {now:yyyy-MM-ddTHH:mm:ss.fffZ} {d.Name} mgd 4321 UI_COMMIT [junos@2636.1.1.1.2.63 username=\"netadmin\"] User 'netadmin' requested 'commit' operation",
                $"<28>1 {now:yyyy-MM-ddTHH:mm:ss.fffZ} {d.Name} sshd 9876 - - Failed password for netadmin from 10.250.0.201 port 51022 ssh2",
            },
        };
        return messages[_rng.Next(messages.Length)];
    }

    private static TrapV2Message Trap(string oid, params Variable[] extra) =>
        new(Messenger.NextRequestId, VersionCode.V2, new OctetString("public"), new ObjectIdentifier(oid), 123456, extra);

    private static TrapV2Message LinkTrap(int ifIndex, bool down) =>
        Trap(down ? "1.3.6.1.6.3.1.1.5.3" : "1.3.6.1.6.3.1.1.5.4",
            new Variable(new ObjectIdentifier($"1.3.6.1.2.1.2.2.1.1.{ifIndex}"), new Integer32(ifIndex)),
            new Variable(new ObjectIdentifier($"1.3.6.1.2.1.2.2.1.7.{ifIndex}"), new Integer32(1)),
            new Variable(new ObjectIdentifier($"1.3.6.1.2.1.2.2.1.8.{ifIndex}"), new Integer32(down ? 2 : 1)));
}
