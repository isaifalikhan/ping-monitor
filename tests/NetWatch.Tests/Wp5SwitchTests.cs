using System.Globalization;
using Lextm.SharpSnmpLib;
using NetWatch.Alerts;
using NetWatch.Collectors.Processing;
using NetWatch.Collectors.Snmp;
using NetWatch.Core;
using NetWatch.Core.Monitoring;
using NetWatch.Web.Topology;

namespace NetWatch.Tests;

/// <summary>WP5: LLDP/CDP and duplex parsing from snmpwalk output, link flaps, port checks, health profiles, dependency suggestions.</summary>
public sealed class Wp5SwitchTests
{
    /// <summary>Loads "snmpwalk -On" output (STRING / INTEGER / Hex-STRING / Timeticks lines).</summary>
    internal static Dictionary<string, ISnmpData> Walk(string file)
    {
        var map = new Dictionary<string, ISnmpData>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Samples", "Snmp", file)))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf(" = ", StringComparison.Ordinal);
            var oid = line[..eq].TrimStart('.');
            var rest = line[(eq + 3)..];
            var colon = rest.IndexOf(": ", StringComparison.Ordinal);
            var (type, value) = (rest[..colon], rest[(colon + 2)..]);
            map[oid] = type switch
            {
                "INTEGER" => new Integer32(int.Parse(value, CultureInfo.InvariantCulture)),
                "Hex-STRING" => new OctetString(value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray()),
                "Timeticks" => new TimeTicks(uint.Parse(value.Trim('(').Split(')')[0], CultureInfo.InvariantCulture)),
                _ => new OctetString(value.Trim('"')),
            };
        }

        return map;
    }

    /// <summary>The rows of one column, keyed by the index after the column OID (as SnmpClient.WalkColumnAsync returns them).</summary>
    internal static Dictionary<string, ISnmpData> Column(Dictionary<string, ISnmpData> walk, string column) =>
        walk.Where(kv => kv.Key.StartsWith(column + ".", StringComparison.Ordinal)).ToDictionary(kv => kv.Key[(column.Length + 1)..], kv => kv.Value, StringComparer.Ordinal);

    [Fact]
    public void Parses_lldp_neighbours_from_a_cisco_walk()
    {
        var w = Walk("cisco-c9300-lldp.walk");
        var n = NeighborParser.ParseLldp(Column(w, Oids.LldpRemChassisIdSubtype), Column(w, Oids.LldpRemChassisId), Column(w, Oids.LldpRemPortIdSubtype),
            Column(w, Oids.LldpRemPortId), Column(w, Oids.LldpRemPortDesc), Column(w, Oids.LldpRemSysName), Column(w, Oids.LldpRemSysDesc),
            Column(w, Oids.LldpLocPortId), Column(w, Oids.LldpLocPortDesc), Column(w, Oids.LldpRemManAddrIfSubtype)).OrderBy(x => x.LocalPortNum).ToList();

        Assert.Equal(2, n.Count);
        var server = n[0];
        Assert.Equal((9, "Gi1/0/1", "SRV-APP-01"), (server.LocalPortNum, server.LocalPortId, server.RemoteName));
        Assert.Equal("B4:96:91:0E:3A:12", server.RemoteChassisId); // MAC chassis subtype
        Assert.Equal("B4:96:91:0E:3A:12", server.RemotePort);      // MAC port subtype
        Assert.Null(server.RemoteAddress);

        var core = n[1];
        Assert.Equal(("CORE-01.corp.local", "1/1/49", "10.0.0.1"), (core.RemoteName, core.RemotePort, core.RemoteAddress));
        Assert.Equal("70:10:6F:2A:11:C0", core.RemoteChassisId);
        Assert.StartsWith("Aruba JL659A", core.RemotePlatform);

        var interfaces = new Dictionary<int, (string, string?)> { [9] = ("Gi1/0/1", "GigabitEthernet1/0/1"), [57] = ("Te1/1/1", "TenGigabitEthernet1/1/1") };
        Assert.Equal(57, NeighborParser.ResolveLocalIfIndex(core, interfaces));
        Assert.Equal(9, NeighborParser.ResolveLocalIfIndex(server with { LocalPortId = null, LocalPortDesc = null }, interfaces)); // falls back to port number
        Assert.Null(NeighborParser.ResolveLocalIfIndex(server with { LocalPortNum = 500, LocalPortId = "x", LocalPortDesc = null }, interfaces));
    }

    [Fact]
    public void Parses_cdp_cache_and_deduplicates_with_lldp()
    {
        var w = Walk("cisco-c9300-cdp.walk");
        var cdp = NeighborParser.ParseCdp(Column(w, Oids.CdpCacheDeviceId), Column(w, Oids.CdpCacheDevicePort), Column(w, Oids.CdpCacheAddressType),
            Column(w, Oids.CdpCacheAddress), Column(w, Oids.CdpCachePlatform)).OrderBy(x => x.LocalPortNum).ToList();
        Assert.Equal(2, cdp.Count);
        Assert.Equal((12, "SEP001122334455", "10.10.20.5", "Cisco IP Phone 8845"), (cdp[0].LocalIfIndex!.Value, cdp[0].RemoteName, cdp[0].RemoteAddress, cdp[0].RemotePlatform));
        Assert.Equal("CORE-01.corp.local", cdp[1].RemoteName); // "(serial)" suffix removed
        Assert.Equal("TenGigabitEthernet1/1/49", cdp[1].RemotePort);

        // The same uplink seen by LLDP (port Te1/1/1 = ifIndex 57) and CDP collapses into one row that keeps the ifIndex.
        var lldp = new NeighborReading("LLDP", 57, null, "Te1/1/1", null, "CORE-01.corp.local", "70:10:6F:2A:11:C0", "1/1/49", null, null, null);
        var merged = NeighborParser.Deduplicate([lldp, cdp[1] with { LocalPortId = "Te1/1/1" }]);
        var one = Assert.Single(merged);
        Assert.Equal("LLDP", one.Protocol);
        Assert.Equal("10.0.0.1", one.RemoteAddress);
        Assert.Equal(57, one.LocalIfIndex);
    }

    [Fact]
    public void Duplex_values_and_port_checks()
    {
        var duplex = Column(Walk("etherlike-duplex.walk"), Oids.Dot3StatsDuplexStatus);
        Assert.Equal(3, SnmpValues.Int64(duplex["9"]));
        Assert.True(PortChecks.IsHalfDuplex(1, (int?)SnmpValues.Int64(duplex["10"])));
        Assert.False(PortChecks.IsHalfDuplex(2, 2)); // down ports are not judged
        Assert.False(PortChecks.IsHalfDuplex(1, (int?)SnmpValues.Int64(duplex["11"])));

        Assert.True(PortChecks.IsBelowExpectedSpeed(1, 1_000_000_000, 10_000_000_000));
        Assert.False(PortChecks.IsBelowExpectedSpeed(1, 10_000_000_000, 10_000_000_000));
        Assert.False(PortChecks.IsBelowExpectedSpeed(1, 1_000_000_000, null));
        Assert.False(PortChecks.IsBelowExpectedSpeed(2, 100_000_000, 1_000_000_000));
        Assert.Equal("10 Gbit/s", PortChecks.SpeedText(10_000_000_000));
        Assert.Equal("100 Mbit/s", PortChecks.SpeedText(100_000_000));
    }

    [Fact]
    public void Flap_tracker_counts_status_changes_and_hidden_bounces()
    {
        var t = new FlapTracker();
        var t0 = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromMinutes(10);
        t.Observe(t0, 1, 1000, false, window);                     // first sight: no change
        Assert.Equal(0, t.InWindow);
        t.Observe(t0.AddMinutes(1), 2, 7000, false, window);       // down
        t.Observe(t0.AddMinutes(2), 1, 13000, false, window);      // up
        Assert.Equal(2, t.InWindow);
        t.Observe(t0.AddMinutes(3), 1, 19000, false, window);      // still up but ifLastChange moved: down+up between polls
        Assert.Equal(4, t.InWindow);
        t.Observe(t0.AddMinutes(4), 1, 19000, false, window);      // stable
        Assert.Equal(4, t.InWindow);
        t.Observe(t0.AddMinutes(5), 1, 50, true, window);          // device rebooted: ifLastChange reset is not a flap
        Assert.Equal(4, t.InWindow);
        t.Observe(t0.AddMinutes(14), 1, 50, false, window);        // the window moves on
        Assert.Equal(0, t.InWindow);
        Assert.Equal(4, t.Last24h);
        t.Observe(t0.AddHours(25), 1, 50, false, window);
        Assert.Equal(0, t.Last24h);
    }

    [Fact]
    public void Arubacx_profile_maps_fans_psus_and_temperatures()
    {
        var w = Walk("arubacx-health.walk");
        var profile = new SnmpHealthProfileStore().Find("HPE Aruba", "1.3.6.1.4.1.47196.4.1.1.1.100");
        Assert.NotNull(profile);
        Assert.False(profile.Verified);
        var sensors = profile.Sensors.SelectMany(t => ProfileHealthReader.Map(t, Column(w, t.NameColumn), Column(w, t.StateColumn!), t.ValueColumn is null ? [] : Column(w, t.ValueColumn))).ToList();

        Assert.Equal(SensorState.Critical, Assert.Single(sensors, s => s.Kind == SensorKind.Fan && s.Name == "1/1/2").State);
        Assert.Equal(SensorState.Ok, Assert.Single(sensors, s => s.Kind == SensorKind.Fan && s.Name == "1/1/1").State);
        Assert.Single(sensors, s => s.Kind == SensorKind.PowerSupply); // the empty bay is skipped
        var temp = Assert.Single(sensors, s => s.Kind == SensorKind.Temperature);
        Assert.Equal((46.5, "C", SensorState.Ok), (temp.Value!.Value, temp.Unit!, temp.State));
        Assert.Null(new SnmpHealthProfileStore().Find("Cisco", null)); // Cisco keeps its vendor code
    }

    [Fact]
    public void Profile_file_overrides_built_in_profiles()
    {
        var file = SnmpHealthProfileStore.ParseFile("""
            // corrected after the pilot walk
            [{ "vendor": "HPE Aruba", "verified": true, "cpuColumn": "1.3.6.1.4.1.47196.4.1.1.3.11.1.1.1.4",
               "sensors": [{ "kind": "Fan", "nameColumn": "1.2.3.5", "stateColumn": "1.2.3.6", "ok": ["ok"], "critical": ["fault"] }] },
             { "vendor": "Extreme", "sensors": [] }]
            """);
        var merged = SnmpHealthProfileStore.Merge(SnmpHealthProfileStore.BuiltIn, file);
        var cx = Assert.Single(merged, p => p.Vendor == "HPE Aruba");
        Assert.True(cx.Verified);
        Assert.Equal("1.2.3.5", Assert.Single(cx.Sensors).NameColumn);
        Assert.Contains(merged, p => p.Vendor == "Extreme");
        Assert.Equal(SensorState.Unknown, ProfileHealthReader.StateOf(cx.Sensors[0], "weird"));
    }

    [Fact]
    public void Suggests_parents_from_links_rooted_at_the_core()
    {
        TopologySuggester.Node N(int id, DeviceType type = DeviceType.Switch, int? parent = null, bool core = false) => new(id, $"D{id}", type, parent, core);
        var nodes = new List<TopologySuggester.Node>
        {
            N(1, core: true), N(2, parent: 1), N(3), N(4, parent: 2), N(5, DeviceType.Server), N(6, DeviceType.Server, parent: 3), N(7, DeviceType.VM),
        };
        // 1 - 2 - 4, 1 - 3 - 5, 3 - 6, 5 - 7 (a VM linked to a server: servers are never parents of others)
        var edges = new List<TopologySuggester.Edge> { new(1, 2, "LLDP"), new(2, 4, "LLDP"), new(1, 3, "CDP"), new(3, 1, "LLDP"), new(3, 5, "LLDP"), new(6, 3, "LLDP"), new(5, 7, "LLDP") };
        var s = TopologySuggester.Suggest(nodes, edges).ToDictionary(x => x.DeviceId);

        Assert.False(s.ContainsKey(2)); // already correct
        Assert.False(s.ContainsKey(4));
        Assert.False(s.ContainsKey(6));
        Assert.Equal(1, s[3].SuggestedParentId);
        Assert.Equal(3, s[5].SuggestedParentId);
        Assert.Equal(5, s[7].SuggestedParentId); // a server may still be the parent of its VM through a direct link
        Assert.False(s.ContainsKey(1));          // the core gets no parent
    }

    [Fact]
    public void Default_rules_cover_the_switch_gaps()
    {
        var rules = DefaultRules.Create(DateTime.UtcNow);
        Assert.Equal(StateKeys.InterfaceFlapping, Assert.Single(rules, r => r.Name == "Port flapping").Key);
        Assert.Contains("\"importantInterfacesOnly\":true", Assert.Single(rules, r => r.Name == "Important port half duplex").ScopeJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StateKeys.InterfaceSpeedLow, Assert.Single(rules, r => r.Name == "Important port below expected speed").Key);
        var m = new Core.Configuration.MonitoringSettings();
        Assert.Equal((4, 10, true), (m.FlapChanges, m.FlapWindowMin, m.DiscoverNeighbors));
    }
}
