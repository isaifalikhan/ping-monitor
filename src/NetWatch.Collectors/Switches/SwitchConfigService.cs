using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Processing;
using NetWatch.Core;
using NetWatch.Core.Credentials;
using NetWatch.Data.Config;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NetWatch.Collectors.Switches;

/// <summary>Add or remove a static MAC address on a switch port.</summary>
public sealed record StaticMacRequest(int DeviceId, int InterfaceId, string Mac, int Vlan, bool Add);

/// <summary>What happened. <see cref="Command"/> is the configuration command that was sent (or would have been).</summary>
public sealed record StaticMacOutcome(bool Ok, string Message, string? Command = null, string? Transcript = null);

/// <summary>
/// Changes static MAC addresses on TP-Link and Juniper switches over SSH, typing the same commands an engineer would in
/// PuTTY, and saves the configuration. One change at a time per switch. The switch's SSH host key is pinned on first
/// use (as for Linux servers). Simulated switches use <see cref="SimulatedSwitchShell"/>.
/// </summary>
public sealed class SwitchConfigService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CredentialResolver _credentials;
    private readonly InventoryCache _inventory;
    private readonly MacTableStore _macs;
    private readonly SimulatedSwitchConfig _simulated;
    private readonly SettingsService _settings;
    private readonly ILogger<SwitchConfigService> _logger;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<int, SimulatedSwitchShell> _simShells = new();

    public SwitchConfigService(IServiceScopeFactory scopeFactory, CredentialResolver credentials, InventoryCache inventory, MacTableStore macs,
                               SimulatedSwitchConfig simulated, SettingsService settings, ILogger<SwitchConfigService> logger)
    {
        _scopeFactory = scopeFactory;
        _credentials = credentials;
        _inventory = inventory;
        _macs = macs;
        _simulated = simulated;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The CLI dialect for a vendor name, or null when changing MACs is not supported for it.</summary>
    public static SwitchCliKind? KindOf(string? vendor) =>
        vendor is null ? null
        : vendor.Contains("TP-Link", StringComparison.OrdinalIgnoreCase) || vendor.Contains("TPLink", StringComparison.OrdinalIgnoreCase) ? SwitchCliKind.TpLink
        : vendor.Contains("Juniper", StringComparison.OrdinalIgnoreCase) ? SwitchCliKind.Junos
        : null;

    /// <summary>Checks a MAC typed in any notation: 12 hex digits, unicast, not all zeros. Returns the hex or an error.</summary>
    public static (string? Hex, string? Error) ValidateMac(string? text)
    {
        var hex = MacTableStore.NormalizeHex(text ?? string.Empty);
        if (hex.Length != 12)
        {
            return (null, "Enter a full MAC address (12 hex digits, e.g. 00:1A:2B:3C:4D:5E).");
        }

        if (hex == "000000000000")
        {
            return (null, "00:00:00:00:00:00 is not a valid MAC address.");
        }

        if ((byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 1) == 1)
        {
            return (null, "That is a multicast/broadcast address; a static entry needs a device (unicast) MAC.");
        }

        return (hex, null);
    }

    public async Task<StaticMacOutcome> ChangeAsync(StaticMacRequest request, CancellationToken ct)
    {
        var (hex, macError) = ValidateMac(request.Mac);
        if (hex is null)
        {
            return new StaticMacOutcome(false, macError!);
        }

        if (request.Vlan is < 1 or > 4094)
        {
            return new StaticMacOutcome(false, "VLAN must be between 1 and 4094.");
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
        var iface = await db.Interfaces.AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.InterfaceId && i.DeviceId == request.DeviceId, ct);
        if (device is null || iface is null)
        {
            return new StaticMacOutcome(false, "Switch or port not found.");
        }

        if (device.Type is not (DeviceType.Switch or DeviceType.Router or DeviceType.Other))
        {
            return new StaticMacOutcome(false, "Static MAC addresses can only be changed on switches.");
        }

        if (KindOf(device.Vendor) is not { } kind)
        {
            return new StaticMacOutcome(false, $"Changing static MACs is supported on TP-Link and Juniper switches; this device's vendor is '{device.Vendor ?? "unknown"}'.");
        }

        var cliPort = kind == SwitchCliKind.TpLink ? SwitchCliCommands.TpLinkInterface(iface.Name, iface.Description) : SwitchCliCommands.JunosInterface(iface.Name, iface.Description);
        if (cliPort is null)
        {
            return new StaticMacOutcome(false, $"Cannot tell the CLI name of port '{iface.Name}' ({iface.Description}).");
        }

        var change = new StaticMacChange(request.Add, hex, request.Vlan, iface.Name, iface.Description);
        var command = kind == SwitchCliKind.TpLink ? SwitchCliCommands.TpLinkCommand(change, cliPort) : SwitchCliCommands.JunosCommand(change, "<vlan-name>", cliPort);
        var gate = _locks.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(60), ct))
        {
            return new StaticMacOutcome(false, "Another change on this switch is still running; try again in a minute.", command);
        }

        try
        {
            CliResult result;
            if (_inventory.GetSnapshot(device.Id)?.IsSimulated == true)
            {
                var shell = _simShells.GetOrAdd(device.Id, _ => new SimulatedSwitchShell(kind, device.Name.Replace(' ', '-')));
                result = await SwitchCliDialog.RunAsync(kind, shell, change, cliPort, null, ct);
                if (result.Ok)
                {
                    _simulated.Apply(device.Id, iface.IfIndex, ColonUpper(hex), request.Vlan, request.Add);
                }
            }
            else
            {
                var credentialId = await db.DeviceCredentials.AsNoTracking()
                    .Where(c => c.DeviceId == device.Id && c.Purpose == CredentialPurpose.Management).Select(c => (int?)c.CredentialProfileId).FirstOrDefaultAsync(ct);
                var credential = credentialId is { } cid ? await _credentials.GetManagementAsync(cid, ct) : null;
                if (credential is null || credential.Kind != CredentialKind.Ssh || string.IsNullOrWhiteSpace(credential.Secret.Username))
                {
                    return new StaticMacOutcome(false, "This switch has no SSH credential. Create an SSH credential (Settings → Credentials) with a switch admin user, " +
                                                       "then choose it as the switch's \"SSH credential (CLI)\" on Edit device.", command);
                }

                result = await RunOverSshAsync(device.Id, device.IpAddress, device.SshHostKeyFingerprint, credential.Secret, kind, change, cliPort, db, ct);
            }

            if (result.Ok)
            {
                _macs.ApplyStatic(device.Id, new MacPortEntry(ColonUpper(hex), request.Vlan, iface.IfIndex, iface.Id, iface.Name, Static: true), request.Add);
            }
            else
            {
                _logger.LogWarning("Static MAC change on {Device} {Port} failed: {Message}", device.Name, iface.Name, result.Message);
            }

            return new StaticMacOutcome(result.Ok, result.Message, command, result.Transcript);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CliResult> RunOverSshAsync(int deviceId, string host, string? pinned, CredentialSecret secret, SwitchCliKind kind, StaticMacChange change,
                                                  string cliPort, NetWatchDbContext db, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(_settings.Servers.TimeoutSec, 15));
        string? seen = null;
        try
        {
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrWhiteSpace(secret.PrivateKey))
            {
                using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(secret.PrivateKey));
                var key = string.IsNullOrEmpty(secret.Password) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, secret.Password);
                methods.Add(new PrivateKeyAuthenticationMethod(secret.Username!, key));
            }

            if (!string.IsNullOrEmpty(secret.Password))
            {
                methods.Add(new PasswordAuthenticationMethod(secret.Username!, secret.Password));
                // Many switches ask for the password through keyboard-interactive.
                var interactive = new KeyboardInteractiveAuthenticationMethod(secret.Username!);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var p in e.Prompts)
                    {
                        p.Response = secret.Password;
                    }
                };
                methods.Add(interactive);
            }

            var info = new ConnectionInfo(host, _settings.Servers.SshPort, secret.Username!, methods.ToArray()) { Timeout = timeout };
            using var client = new SshClient(info);
            client.HostKeyReceived += (_, e) =>
            {
                seen = e.FingerPrintSHA256;
                e.CanTrust = pinned is null || string.Equals(pinned, e.FingerPrintSHA256, StringComparison.Ordinal);
            };

            await client.ConnectAsync(ct).WaitAsync(timeout + TimeSpan.FromSeconds(5), ct);
            if (pinned is null && seen is not null)
            {
                await db.Devices.Where(d => d.Id == deviceId).ExecuteUpdateAsync(u => u.SetProperty(d => d.SshHostKeyFingerprint, seen), ct);
            }

            using var stream = client.CreateShellStream("vt100", 250, 60, 0, 0, 65536);
            var shell = new SshCliShell(stream, timeout);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            var result = await SwitchCliDialog.RunAsync(kind, shell, change, cliPort, secret.Password, cts.Token);
            client.Disconnect();
            return result;
        }
        catch (SshConnectionException) when (pinned is not null && seen is not null && seen != pinned)
        {
            return new CliResult(false, $"SSH host key of the switch changed (expected SHA256:{pinned}, got SHA256:{seen}). " +
                                        "If the switch was replaced or reset, clear the stored host key on the device page.", string.Empty);
        }
        catch (SshAuthenticationException)
        {
            return new CliResult(false, "SSH login to the switch failed (check the user name and password of its SSH credential).", string.Empty);
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new CliResult(false, ex is OperationCanceledException ? "The switch took too long to answer." : $"SSH: {ex.Message} (is SSH enabled on the switch?)", string.Empty);
        }
    }

    private static string ColonUpper(string hex) => SwitchCliCommands.ColonMac(hex).ToUpperInvariant();
}
