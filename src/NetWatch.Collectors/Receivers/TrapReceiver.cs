using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Scheduling;
using NetWatch.Collectors.Snmp;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Config;

namespace NetWatch.Collectors.Receivers;

/// <summary>
/// Turns decoded traps into stored traps and alert signals. linkUp/linkDown and cold/warm start traps also
/// trigger an immediate poll of the device, so port and reboot alerts appear within seconds instead of at the
/// next poll.
/// </summary>
public sealed class TrapPipeline
{
    private readonly InventoryCache _inventory;
    private readonly EventIngestService _ingest;
    private readonly IAlertSignalSink _alerts;
    private readonly PollScheduler _scheduler;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SettingsService _settings;
    private readonly ReceiverStats _stats;
    private readonly TimeProvider _time;

    public TrapPipeline(InventoryCache inventory, EventIngestService ingest, IAlertSignalSink alerts, PollScheduler scheduler,
        IServiceScopeFactory scopeFactory, SettingsService settings, ReceiverStats stats, TimeProvider time)
    {
        _inventory = inventory;
        _ingest = ingest;
        _alerts = alerts;
        _scheduler = scheduler;
        _scopeFactory = scopeFactory;
        _settings = settings;
        _stats = stats;
        _time = time;
    }

    public async Task AcceptAsync(IPAddress source, DecodedTrap trap, CancellationToken ct)
    {
        // A relayed SNMPv1 trap names the originating device in agent-addr.
        var device = (trap.AgentAddress is { } agent ? _inventory.FindByIp(agent) : null) ?? _inventory.FindByIp(source);
        if (device is null)
        {
            _stats.Unknown();
            if (!_settings.Receivers.AcceptUnknownSources)
            {
                _stats.Drop();
                return;
            }
        }

        if (!_ingest.Admit())
        {
            return;
        }

        string? interfaceName = null;
        if (device is not null && trap.IsLinkEvent && trap.IfIndex is { } ifIndex)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            interfaceName = await db.Interfaces.AsNoTracking().Where(i => i.DeviceId == device.Id && i.IfIndex == ifIndex).Select(i => i.Name).FirstOrDefaultAsync(ct);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var summary = trap.Summary(interfaceName);
        _ingest.Enqueue(new SnmpTrap
        {
            ReceivedUtc = now,
            SourceIp = (source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source).ToString(),
            DeviceId = device?.Id,
            Version = trap.Version,
            TrapOid = trap.TrapOid,
            TrapName = trap.Name,
            Severity = trap.Severity,
            Summary = summary,
            VarbindsJson = trap.VarbindsJson(),
        });

        if (device is null)
        {
            return;
        }

        _alerts.Publish(new EventSignal(device.Id, EventKeys.SnmpTrap, summary, now, trap.Severity, trap.Name, null, trap.TrapOid));
        if (trap.IsLinkEvent || trap.IsRestart)
        {
            _scheduler.PollNow(device.Id);
        }
    }
}

/// <summary>SNMP trap/inform listener on UDP 162. Answers v1/v2c informs; decrypts v3 traps with the configured SNMPv3 users.</summary>
public sealed class TrapReceiver : BackgroundService
{
    private readonly TrapPipeline _pipeline;
    private readonly CredentialResolver _credentials;
    private readonly SettingsService _settings;
    private readonly ReceiverStats _stats;
    private readonly ILogger<TrapReceiver> _logger;
    private UserRegistry _users = new();
    private DateTime _usersLoadedUtc = DateTime.MinValue;

    public TrapReceiver(TrapPipeline pipeline, CredentialResolver credentials, SettingsService settings, ReceiverStats stats, ILogger<TrapReceiver> logger)
    {
        _pipeline = pipeline;
        _credentials = credentials;
        _settings = settings;
        _stats = stats;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var s = _settings.Receivers;
        if (!s.TrapsEnabled)
        {
            _stats.SetListener("traps", "disabled");
            return;
        }

        var name = $"traps/udp:{s.TrapPort}";
        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetworkV6);
            udp.Client.DualMode = true;
            udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, s.TrapPort));
            _stats.SetListener(name, "listening");
            _logger.LogInformation("SNMP trap receiver listening on UDP {Port}", s.TrapPort);
        }
        catch (SocketException ex)
        {
            _stats.SetListener(name, $"error: {ex.Message}");
            _logger.LogError(ex, "Cannot listen for SNMP traps on UDP {Port}", s.TrapPort);
            return;
        }

        using (udp)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(stoppingToken);
                    await RefreshUsersAsync(stoppingToken);
                    await HandleAsync(udp, r, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, "Trap receive error");
                }
            }
        }
    }

    private async Task HandleAsync(UdpClient udp, UdpReceiveResult r, CancellationToken ct)
    {
        _stats.Trap();
        IList<ISnmpMessage> messages;
        try
        {
            messages = MessageFactory.ParseMessages(r.Buffer, 0, r.Buffer.Length, _users);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stats.ParseError();
            _logger.LogDebug(ex, "Undecodable trap from {Source}", r.RemoteEndPoint);
            return;
        }

        foreach (var message in messages)
        {
            if (message is InformRequestMessage inform && inform.Version != VersionCode.V3)
            {
                // Acknowledge informs so the agent stops retransmitting.
                var response = new ResponseMessage(inform.RequestId(), inform.Version, inform.Parameters.UserName, ErrorCode.NoError, 0, inform.Pdu().Variables);
                await udp.SendAsync(response.ToBytes(), r.RemoteEndPoint, ct);
            }

            var trap = TrapDecoder.Decode(message);
            if (trap is null)
            {
                continue;
            }

            await _pipeline.AcceptAsync(r.RemoteEndPoint.Address, trap, ct);
        }
    }

    /// <summary>Rebuilds the SNMPv3 user registry from the credential profiles every 5 minutes.</summary>
    private async Task RefreshUsersAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _usersLoadedUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _usersLoadedUtc = DateTime.UtcNow;
        var registry = new UserRegistry();
        foreach (var secret in await _credentials.GetAllSnmpV3Async(ct))
        {
            try
            {
                registry.Add(new OctetString(secret.Username ?? string.Empty), SnmpClient.BuildPrivacy(secret));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Skipping SNMPv3 user {User} for traps", secret.Username);
            }
        }

        _users = registry;
    }
}
