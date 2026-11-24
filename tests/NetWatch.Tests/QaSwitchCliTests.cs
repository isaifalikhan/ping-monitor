
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Collectors;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Processing;
using NetWatch.Collectors.Snmp;
using NetWatch.Collectors.Switches;
using NetWatch.Core;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data;
using NetWatch.Data.Config;

namespace NetWatch.Tests;

/// <summary>
/// Adding/removing static MACs on TP-Link and Juniper switches over SSH (the PuTTY commands), and the device name per
/// MAC. The real SSH transport can only be proven on the switches themselves (PENDING PILOT); everything above it is
/// run here against <see cref="SimulatedSwitchShell"/>, which answers like the real CLIs.
/// </summary>
public sealed class QaSwitchCliTests : IAsyncLifetime
{
    private const string Mac = "3C:30:00:0A:00:05";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-switchcli-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddNetWatchData(new NetWatchPaths(_root));
        services.AddNetWatchCollectors();
        services.AddSingleton<NetWatch.Core.Alerts.IAlertSignalSink, NullSignalSink>();
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

    private static StaticMacChange Change(bool add = true, int vlan = 10) => new(add, MacTableStore.NormalizeHex(Mac), vlan, "Gi1/0/5", null);

    [Theory]
    [InlineData("Gi1/0/5", null, "gigabitEthernet 1/0/5")]
    [InlineData("1/0/12", "gigabitEthernet 1/0/12 : copper", "gigabitEthernet 1/0/12")]
    [InlineData("GigabitEthernet1/0/48", null, "gigabitEthernet 1/0/48")]
    [InlineData("Te1/0/25", null, "ten-gigabitEthernet 1/0/25")]
    [InlineData("ten-gigabitEthernet 1/0/26 : fiber", null, "ten-gigabitEthernet 1/0/26")]
    [InlineData("Fa1/0/3", null, "fastEthernet 1/0/3")]
    public void Tp_link_port_names_become_the_cli_interface(string name, string? description, string expected) =>
        Assert.Equal(expected, SwitchCliCommands.TpLinkInterface(name, description));

    [Theory]
    [InlineData("ge-0/0/5", "ge-0/0/5.0")]
    [InlineData("ge-0/0/5.0", "ge-0/0/5.0")]
    [InlineData("xe-0/1/0", "xe-0/1/0.0")]
    [InlineData("mge-0/0/12.0", "mge-0/0/12.0")]
    public void Junos_port_names_become_the_logical_interface(string name, string expected) =>
        Assert.Equal(expected, SwitchCliCommands.JunosInterface(name, null));

    [Theory]
    [InlineData("vlan.10")]
    [InlineData("ifIndex 49153")]
    [InlineData("ge-0/0/5; rm -rf")]
    public void Unknown_or_unsafe_port_names_are_not_turned_into_commands(string name)
    {
        Assert.Null(SwitchCliCommands.TpLinkInterface(name, null));
        Assert.Null(SwitchCliCommands.JunosInterface(name, null));
    }

    [Theory]
    [InlineData("00:1a:2b:3c:4d:5e", "001A2B3C4D5E")]
    [InlineData("00-1A-2B-3C-4D-5E", "001A2B3C4D5E")]
    [InlineData("001a.2b3c.4d5e", "001A2B3C4D5E")]
    public void Mac_addresses_in_any_notation_are_accepted(string input, string hex) => Assert.Equal(hex, SwitchConfigService.ValidateMac(input).Hex);

    [Theory]
    [InlineData("00:1a:2b")]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("01:00:5e:00:00:01")]
    [InlineData("ff:ff:ff:ff:ff:ff")]
    [InlineData("00:1a:2b:3c:4d:5e; reload")]
    public void Short_multicast_broadcast_and_garbage_macs_are_refused(string input) => Assert.NotNull(SwitchConfigService.ValidateMac(input).Error);

    [Fact]
    public async Task Tp_link_add_types_the_putty_commands_saves_and_never_logs_the_enable_password()
    {
        var shell = new SimulatedSwitchShell(SwitchCliKind.TpLink, "SW-ACC-01", enablePassword: "En@ble-2026");
        var result = await SwitchCliDialog.RunAsync(SwitchCliKind.TpLink, shell, Change(), "gigabitEthernet 1/0/5", "En@ble-2026", CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(["enable", "En@ble-2026", "configure", "mac address-table static 3c:30:00:0a:00:05 vid 10 interface gigabitEthernet 1/0/5", "end",
            "copy running-config startup-config"], shell.Received);
        Assert.Contains("3c:30:00:0a:00:05|10|gigabitEthernet 1/0/5", shell.Statics);
        Assert.DoesNotContain("En@ble-2026", result.Transcript);
        Assert.Contains("Saving user config OK!", result.Transcript);
    }

    [Fact]
    public async Task Tp_link_remove_of_a_missing_entry_reports_the_switch_error_and_does_not_save()
    {
        var shell = new SimulatedSwitchShell(SwitchCliKind.TpLink, "SW-ACC-01", startPrivileged: true);
        var result = await SwitchCliDialog.RunAsync(SwitchCliKind.TpLink, shell, Change(add: false), "gigabitEthernet 1/0/5", null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("The entry does not exist", result.Message);
        Assert.Contains("end", shell.Received);
        Assert.DoesNotContain("copy running-config startup-config", shell.Received);
    }

    [Fact]
    public async Task Tp_link_add_then_remove_round_trips()
    {
        var shell = new SimulatedSwitchShell(SwitchCliKind.TpLink, "SW-ACC-01");
        Assert.True((await SwitchCliDialog.RunAsync(SwitchCliKind.TpLink, shell, Change(), "gigabitEthernet 1/0/5", null, CancellationToken.None)).Ok);
        var removed = await SwitchCliDialog.RunAsync(SwitchCliKind.TpLink, shell, Change(add: false), "gigabitEthernet 1/0/5", null, CancellationToken.None);
        Assert.True(removed.Ok, removed.Message);
        Assert.Contains("no mac address-table static 3c:30:00:0a:00:05 vid 10", shell.Received);
        Assert.Empty(shell.Statics);
    }

    [Fact]
    public async Task Tp_link_refusal_and_missing_enable_password_are_reported()
    {
        var refusing = new SimulatedSwitchShell(SwitchCliKind.TpLink, "SW-ACC-01") { RefuseWith = "Error: The VLAN does not exist." };
        var refused = await SwitchCliDialog.RunAsync(SwitchCliKind.TpLink, refusing, Change(vlan: 999), "gigabitEthernet 1/0/5", null, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("VLAN does not exist", refused.Message);

        var locked = new SimulatedSwitchShell(SwitchCliKind.TpLink, "SW-ACC-01", enablePassword: "secret-1");
        var noPassword = await SwitchCliDialog.RunAsync(SwitchCliKind.TpLink, locked, Change(), "gigabitEthernet 1/0/5", null, CancellationToken.None);
        Assert.False(noPassword.Ok);
        Assert.Contains("enable password", noPassword.Message);
        Assert.DoesNotContain("configure", locked.Received);

        var wrong = new SimulatedSwitchShell(SwitchCliKind.TpLink, "SW-ACC-01", enablePassword: "secret-1");
        var wrongPassword = await SwitchCliDialog.RunAsync(SwitchCliKind.TpLink, wrong, Change(), "gigabitEthernet 1/0/5", "not-it", CancellationToken.None);
        Assert.False(wrongPassword.Ok);
        Assert.DoesNotContain("configure", wrong.Received);
    }

    [Fact]
    public async Task Junos_add_finds_the_vlan_name_sets_static_mac_and_commits_also_when_logged_in_as_root()
    {
        var shell = new SimulatedSwitchShell(SwitchCliKind.Junos, "EX-CORE-01", startAsRootShell: true);
        var result = await SwitchCliDialog.RunAsync(SwitchCliKind.Junos, shell, Change(), "ge-0/0/5.0", null, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(["cli", "show configuration vlans | display set | no-more", "configure private",
            "set vlans USERS switch-options interface ge-0/0/5.0 static-mac 3c:30:00:0a:00:05", "commit and-quit"], shell.Received);
        Assert.Contains("3c:30:00:0a:00:05|USERS|ge-0/0/5.0", shell.Statics);
    }

    [Fact]
    public async Task Junos_unknown_vlan_and_missing_statement_fail_without_committing()
    {
        var shell = new SimulatedSwitchShell(SwitchCliKind.Junos, "EX-CORE-01");
        var noVlan = await SwitchCliDialog.RunAsync(SwitchCliKind.Junos, shell, Change(vlan: 77), "ge-0/0/5.0", null, CancellationToken.None);
        Assert.False(noVlan.Ok);
        Assert.Contains("VLAN 77 is not configured", noVlan.Message);
        Assert.DoesNotContain("configure private", shell.Received);

        var missing = await SwitchCliDialog.RunAsync(SwitchCliKind.Junos, shell, Change(add: false), "ge-0/0/5.0", null, CancellationToken.None);
        Assert.False(missing.Ok);
        Assert.Contains("statement not found", missing.Message);
        Assert.DoesNotContain("commit and-quit", shell.Received);
        Assert.Equal("exit configuration-mode", shell.Received[^1]);
    }

    [Fact]
    public void Prompts_errors_and_vlan_names_are_read_from_real_looking_output()
    {
        Assert.True(SwitchCliCommands.EndsWithPrompt("\r\nSW-ACC-01(config)#"));
        Assert.True(SwitchCliCommands.EndsWithPrompt("Welcome\r\nadmin@EX-CORE-01> "));
        Assert.True(SwitchCliCommands.EndsWithPrompt("[edit]\r\nadmin@EX-CORE-01# "));
        Assert.False(SwitchCliCommands.EndsWithPrompt("Saving user config"));
        Assert.True(SwitchCliCommands.AsksForPassword("SW>enable\r\nPassword:"));
        Assert.True(SwitchCliCommands.AsksToPage("line\r\nPress any key to continue (Q to quit)"));

        // The echoed command is not mistaken for an error even if it contains an error word.
        Assert.Null(SwitchCliCommands.ErrorIn("SW(config)#description not-found-printer\r\nSW(config)#", "description not-found-printer"));
        Assert.Equal("Error: Bad command", SwitchCliCommands.ErrorIn("SW(config)#foo\r\nError: Bad command\r\nSW(config)#", "foo"));

        const string vlans = "set vlans default vlan-id 1\r\nset vlans USERS vlan-id 10\r\nset vlans VOICE vlan-id 20\r\nset vlans \"bad;name\" vlan-id 30\r\n";
        Assert.Equal("USERS", SwitchCliCommands.JunosVlanName(vlans, 10));
        Assert.Null(SwitchCliCommands.JunosVlanName(vlans, 30));
        Assert.Null(SwitchCliCommands.JunosVlanName(vlans, 99));
    }

    [Fact]
    public void A_new_static_mac_replaces_the_learned_entry_and_shows_at_once()
    {
        var store = new MacTableStore();
        store.Update(1, "SW", DateTime.UtcNow, [new MacPortEntry(Mac, 10, 5, 50, "Gi1/0/5"), new MacPortEntry("3C:10:00:0A:00:06", 10, 6, 60, "Gi1/0/6")], []);

        store.ApplyStatic(1, new MacPortEntry(Mac, 10, 7, 70, "Gi1/0/7", Static: true), add: true);
        var table = store.Get(1)!;
        var e = Assert.Single(table.Entries, x => x.Mac == Mac);
        Assert.True(e.Static);
        Assert.Equal("Gi1/0/7", e.Port);
        Assert.Equal(1, table.MacsPerPort[7]);
        Assert.False(table.MacsPerPort.ContainsKey(5));

        store.ApplyStatic(1, new MacPortEntry(Mac, 10, 7, 70, "Gi1/0/7", Static: true), add: false);
        Assert.DoesNotContain(store.Get(1)!.Entries, x => x.Mac == Mac);
        Assert.Single(store.Get(1)!.Entries);
    }

    [Fact]
    public async Task Device_names_come_from_dns_are_cached_and_failures_retried_after_an_hour()
    {
        var store = new MacTableStore();
        store.Update(1, "SW", DateTime.UtcNow, [], [new ArpReading("10.1.10.5", Mac, 1), new ArpReading("10.1.10.6", "3C:10:00:0A:00:06", 1)]);
        var calls = 0;
        var cache = new HostNameCache(store, null, NullLogger<HostNameCache>.Instance, (ip, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(ip.ToString() == "10.1.10.5" ? "PC-ACC-05.hit.local." : ip.ToString());
        });

        var now = DateTime.UtcNow;
        Assert.Equal(2, await cache.ResolvePendingAsync(now, CancellationToken.None));
        Assert.Equal("pc-acc-05.hit.local", cache.NameOf("10.1.10.5"));
        Assert.Null(cache.NameOf("10.1.10.6")); // the "name" was just the IP again
        Assert.Equal(0, await cache.ResolvePendingAsync(now.AddMinutes(30), CancellationToken.None));
        Assert.Equal(1, await cache.ResolvePendingAsync(now.AddMinutes(61), CancellationToken.None));
        Assert.Equal(3, calls);
    }

    private async Task<(int DeviceId, int InterfaceId)> AddSwitchAsync(string vendor, bool simulated, string port = "Gi1/0/5")
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var device = new Device
        {
            Name = "SW-" + Guid.NewGuid().ToString("N")[..6], IpAddress = $"10.9.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}", Type = DeviceType.Switch, Vendor = vendor,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, StatusSinceUtc = DateTime.UtcNow,
        };
        if (simulated)
        {
            device.Tags.Add(new DeviceTag { Tag = DeviceSnapshot.SimulatedTag });
        }

        device.Interfaces.Add(new NetInterface { IfIndex = 5, Name = port, IfType = 6, SpeedBps = 1_000_000_000, OperStatus = 1, AdminStatus = 1 });
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        await _sp.GetRequiredService<InventoryCache>().ReloadAsync(CancellationToken.None);
        return (device.Id, device.Interfaces[0].Id);
    }

    [Fact]
    public async Task Service_changes_a_simulated_tp_link_switch_and_the_mac_table_shows_it()
    {
        var (deviceId, interfaceId) = await AddSwitchAsync("TP-Link", simulated: true);
        var service = _sp.GetRequiredService<SwitchConfigService>();

        var added = await service.ChangeAsync(new StaticMacRequest(deviceId, interfaceId, "3c-30-00-0a-00-05", 10, Add: true), CancellationToken.None);
        Assert.True(added.Ok, added.Message);
        Assert.Equal("mac address-table static 3c:30:00:0a:00:05 vid 10 interface gigabitEthernet 1/0/5", added.Command);
        var entry = Assert.Single(_sp.GetRequiredService<MacTableStore>().Get(deviceId)!.Entries);
        Assert.True(entry is { Static: true, Mac: Mac, Vlan: 10, InterfaceId: var id } && id == interfaceId);
        Assert.Equal(5, Assert.Single(_sp.GetRequiredService<SimulatedSwitchConfig>().Added(deviceId)).Value);

        var removed = await service.ChangeAsync(new StaticMacRequest(deviceId, interfaceId, Mac, 10, Add: false), CancellationToken.None);
        Assert.True(removed.Ok, removed.Message);
        Assert.Empty(_sp.GetRequiredService<MacTableStore>().Get(deviceId)!.Entries);
        Assert.True(_sp.GetRequiredService<SimulatedSwitchConfig>().IsRemoved(deviceId, Mac, 10));
    }

    [Fact]
    public async Task Service_refuses_other_vendors_bad_input_and_real_switches_without_an_ssh_credential()
    {
        var service = _sp.GetRequiredService<SwitchConfigService>();
        var (cisco, ciscoPort) = await AddSwitchAsync("Cisco", simulated: false);
        Assert.Contains("TP-Link and Juniper", (await service.ChangeAsync(new StaticMacRequest(cisco, ciscoPort, Mac, 10, true), CancellationToken.None)).Message);

        var (tp, tpPort) = await AddSwitchAsync("TP-Link", simulated: false);
        Assert.Contains("multicast", (await service.ChangeAsync(new StaticMacRequest(tp, tpPort, "01:00:5e:00:00:01", 10, true), CancellationToken.None)).Message);
        Assert.Contains("VLAN must be", (await service.ChangeAsync(new StaticMacRequest(tp, tpPort, Mac, 5000, true), CancellationToken.None)).Message);
        Assert.Contains("not found", (await service.ChangeAsync(new StaticMacRequest(tp, ciscoPort, Mac, 10, true), CancellationToken.None)).Message);
        var noCredential = await service.ChangeAsync(new StaticMacRequest(tp, tpPort, Mac, 10, true), CancellationToken.None);
        Assert.False(noCredential.Ok);
        Assert.Contains("no SSH credential", noCredential.Message);
        Assert.Null(_sp.GetRequiredService<MacTableStore>().Get(tp));
    }

    [Fact]
    public async Task A_switch_with_an_ssh_credential_is_not_polled_as_a_linux_server()
    {
        var (deviceId, _) = await AddSwitchAsync("TP-Link", simulated: false);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var profile = new CredentialProfile { Name = "switch-ssh", Kind = CredentialKind.Ssh, DisplayHint = "user: admin",
                SecretBlob = _sp.GetRequiredService<NetWatch.Core.Abstractions.ISecretProtector>().Protect(new NetWatch.Core.Credentials.CredentialSecret { Username = "admin", Password = "Sw1tch-pass" }.ToJson()), CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
            db.CredentialProfiles.Add(profile);
            await db.SaveChangesAsync();
            db.DeviceCredentials.Add(new DeviceCredential { DeviceId = deviceId, Purpose = CredentialPurpose.Management, CredentialProfileId = profile.Id });
            await db.SaveChangesAsync();
        }

        var inventory = _sp.GetRequiredService<InventoryCache>();
        await inventory.ReloadAsync(CancellationToken.None);
        Assert.NotNull(inventory.GetSnapshot(deviceId)!.ManagementCredentialId);
        Assert.Null(await _sp.GetRequiredService<ServerResultProcessor>().PrepareAsync(inventory.GetSnapshot(deviceId)!, CancellationToken.None));
    }

    [Fact]
    public async Task Device_name_prefers_the_monitored_device_then_lldp_then_dns()
    {
        var (switchId, portId) = await AddSwitchAsync("TP-Link", simulated: false);
        int printerId;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var printer = new Device { Name = "PRN-HR-01", IpAddress = "10.1.10.5", Type = DeviceType.Other, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, StatusSinceUtc = DateTime.UtcNow };
            db.Devices.Add(printer);
            db.NeighborLinks.Add(new NeighborLink { DeviceId = switchId, LocalInterfaceId = portId, LocalPort = "Gi1/0/5", Protocol = "LLDP", RemoteName = "SEP-PHONE-0042",
                RemoteChassisId = "3c:20:00:0a:00:05", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            printerId = printer.Id;
        }

        var inventory = _sp.GetRequiredService<InventoryCache>();
        await inventory.ReloadAsync(CancellationToken.None);
        var store = new MacTableStore();
        store.Update(switchId, "SW", DateTime.UtcNow, [], [new ArpReading("10.1.10.5", Mac, 1), new ArpReading("10.1.10.7", "3C:10:00:0A:00:07", 1)]);
        var names = new HostNameCache(store, null, NullLogger<HostNameCache>.Instance, (ip, _) => Task.FromResult<string?>(ip.ToString() == "10.1.10.7" ? "pc-acc-07.hit.local" : null));
        await names.ResolvePendingAsync(DateTime.UtcNow, CancellationToken.None);

        using var s2 = _sp.CreateScope();
        var namer = await NetWatch.Web.Api.MacNamer.CreateAsync(s2.ServiceProvider.GetRequiredService<NetWatchDbContext>(), inventory, names, [switchId], CancellationToken.None);
        Assert.Equal(("PRN-HR-01", "device", printerId), namer.NameOf(switchId, portId, Mac, "10.1.10.5", 2));
        Assert.Equal(("SEP-PHONE-0042", "lldp", (int?)null), namer.NameOf(switchId, portId, "3C:20:00:0A:00:05", null, 2));
        Assert.Equal(("pc-acc-07.hit.local", "dns", (int?)null), namer.NameOf(switchId, portId, "3C:10:00:0A:00:07", "10.1.10.7", 2));
        // A PC behind the phone is not given the phone's name; with one MAC on the port the LLDP neighbour is that device.
        Assert.Equal(((string?)null, (string?)null, (int?)null), namer.NameOf(switchId, portId, "3C:10:00:0A:00:99", null, 2));
        Assert.Equal("SEP-PHONE-0042", namer.NameOf(switchId, portId, "3C:10:00:0A:00:99", null, 1).Name);
    }
}

internal sealed class NullSignalSink : NetWatch.Core.Alerts.IAlertSignalSink
{
    public void Publish(NetWatch.Core.Alerts.AlertSignal signal)
    {
    }
}
