using Lextm.SharpSnmpLib;
using NetWatch.Collectors.Processing;
using NetWatch.Collectors.Snmp;

namespace NetWatch.Tests;

/// <summary>
/// MAC address table per port (BRIDGE / Q-BRIDGE-MIB), ARP (IP-MIB), the MAC/IP search, and TP-Link detection and
/// CPU/memory (TPLINK-SYSMONITOR-MIB). Rows are built the way the SNMP walk returns them: index suffix -> value.
/// </summary>
public sealed class QaMacTableTests
{
    private static Dictionary<string, ISnmpData> Rows(params (string Index, ISnmpData Value)[] rows) => rows.ToDictionary(r => r.Index, r => r.Value);

    private static ISnmpData I(int v) => new Integer32(v);

    private static ISnmpData Mac(params byte[] b) => new OctetString(b);

    [Fact]
    public void Q_bridge_table_maps_bridge_ports_to_ifindex_and_fdb_ids_to_vlans_and_skips_self_entries()
    {
        // fdbId.m1..m6 -> bridge port; FDB 1 is VLAN 10 and FDB 2 is VLAN 20 (Juniper-style internal FDB ids).
        var port = Rows(
            ("1.0.17.136.1.2.3", I(5)),       // 00:11:88:01:02:03 on bridge port 5, FDB 1
            ("2.0.17.136.1.2.4", I(5)),       // phone in VLAN 20 on the same port
            ("1.170.187.204.0.0.1", I(49)),   // AA:BB:CC:00:00:01 on the uplink
            ("1.0.0.0.0.0.0", I(5)),          // zero MAC: ignored
            ("1.2.0.0.0.0.99", I(0)),         // bridge port 0 = the switch itself: ignored
            ("1.2.0.0.0.0.98", I(5)));        // status self (4): ignored
        var status = Rows(("1.0.17.136.1.2.3", I(3)), ("2.0.17.136.1.2.4", I(3)), ("1.170.187.204.0.0.1", I(3)), ("1.2.0.0.0.0.98", I(4)));
        var vlanFdb = Rows(("0.10", I(1)), ("0.20", I(2)));
        var basePorts = Rows(("5", I(10005)), ("49", I(10049)));

        var entries = MacTableParser.ParseQBridge(port, status, vlanFdb, basePorts);

        Assert.Equal(3, entries.Count);
        Assert.Contains(new MacEntryReading(10005, "00:11:88:01:02:03", 10), entries);
        Assert.Contains(new MacEntryReading(10005, "00:11:88:01:02:04", 20), entries);
        Assert.Contains(new MacEntryReading(10049, "AA:BB:CC:00:00:01", 10), entries);
    }

    [Fact]
    public void Q_bridge_without_vlan_map_uses_the_fdb_id_as_vlan_and_without_base_ports_the_bridge_port_as_ifindex()
    {
        // TP-Link style: FDB id = VLAN id, and a switch that has no dot1dBasePortIfIndex.
        var entries = MacTableParser.ParseQBridge(Rows(("30.1.2.3.4.5.6", I(7))), Rows(), Rows(), Rows());
        Assert.Equal([new MacEntryReading(7, "01:02:03:04:05:06", 30)], entries);
    }

    /// <summary>
    /// TP-Link Omada 24-port + 4 SFP+ (sysObjectID 1.3.6.1.4.1.11863.5.106), from a real walk: bridge ports and ifIndexes are
    /// 49153…49180, but dot1qTpFdbPort gives the port position 1…28. All MACs used to be dropped (empty MAC tab).
    /// </summary>
    [Fact]
    public void Tp_link_omada_q_bridge_port_positions_resolve_to_the_bridge_ports_interfaces()
    {
        var basePorts = Rows(Enumerable.Range(49153, 28).Select(p => (p.ToString(), I(p))).ToArray());
        var port = Rows(
            ("210.208.70.12.166.101.85", I(10)),    // D0:46:0C:A6:65:55 on port 10 = gigabitEthernet 1/0/10 = ifIndex 49162
            ("210.208.70.12.166.104.109", I(1)),    // port 1 = ifIndex 49153
            ("210.208.70.12.166.193.163", I(25)),   // port 25 = first SFP+ = ifIndex 49177 (the uplink in the BRIDGE-MIB table too)
            ("210.208.70.12.167.203.244", I(29)));  // no 29th port: dropped
        var entries = MacTableParser.ParseQBridge(port, Rows(), Rows(), basePorts);

        Assert.Equal(3, entries.Count);
        Assert.Contains(new MacEntryReading(49162, "D0:46:0C:A6:65:55", 210), entries);
        Assert.Contains(new MacEntryReading(49153, "D0:46:0C:A6:68:6D", 210), entries);
        Assert.Contains(new MacEntryReading(49177, "D0:46:0C:A6:C1:A3", 210), entries);

        // The VLAN-less BRIDGE-MIB table of the same switch reports bridge port 49177 directly.
        Assert.Equal([new MacEntryReading(49177, "10:5A:95:C2:7B:BD", null)],
            MacTableParser.ParseDot1d(Rows(("16.90.149.194.123.189", I(49177))), Rows(), basePorts));
    }

    /// <summary>Static (configured) addresses used to be dropped: status 5 in the learned table, and the static tables were not read.</summary>
    [Fact]
    public void Static_mac_addresses_are_shown_and_marked_besides_the_learned_ones()
    {
        var basePorts = Rows(Enumerable.Range(49153, 28).Select(p => (p.ToString(), I(p))).ToArray());

        // Learned table: a learned PC on port 3, a static printer (mgmt = 5) on port 7, the switch's own MAC (self = 4).
        var learned = MacTableParser.ParseQBridge(
            Rows(("210.0.17.34.0.0.3", I(3)), ("210.0.17.34.0.0.7", I(7)), ("210.0.17.34.0.0.99", I(1))),
            Rows(("210.0.17.34.0.0.3", I(3)), ("210.0.17.34.0.0.7", I(5)), ("210.0.17.34.0.0.99", I(4))),
            Rows(), basePorts);
        Assert.Equal(
            [new MacEntryReading(49155, "00:11:22:00:00:03", 210, Static: false), new MacEntryReading(49159, "00:11:22:00:00:07", 210, Static: true)],
            learned);

        // Static unicast table (fdbId.mac.receivePort -> PortList): a camera bound to port 12 (byte 2, bit 0x10), silent so not
        // in the learned table; the printer again (port 7 = 0x02 in byte 1); a flooding rule to 8 ports is ignored.
        static ISnmpData PortList(params byte[] b) => new OctetString(b);
        var statics = MacTableParser.ParseQStatic(
            Rows(("210.0.17.34.0.0.12.0", PortList(0x00, 0x10, 0x00, 0x00)),
                 ("210.0.17.34.0.0.7.0", PortList(0x02, 0x00, 0x00, 0x00)),
                 ("210.1.0.94.0.0.1.0", PortList(0xFF, 0x00, 0x00, 0x00))),
            Rows(("210.0.17.34.0.0.12.0", I(3))), Rows(), basePorts);
        Assert.Equal(
            [new MacEntryReading(49159, "00:11:22:00:00:07", 210, Static: true), new MacEntryReading(49164, "00:11:22:00:00:0C", 210, Static: true)],
            statics);

        // Merged: learned PC stays dynamic, printer once (static), the silent camera is added.
        var merged = MacTableParser.Merge(learned, statics);
        Assert.Equal(3, merged.Count);
        Assert.Contains(new MacEntryReading(49155, "00:11:22:00:00:03", 210, Static: false), merged);
        Assert.Contains(new MacEntryReading(49159, "00:11:22:00:00:07", 210, Static: true), merged);
        Assert.Contains(new MacEntryReading(49164, "00:11:22:00:00:0C", 210, Static: true), merged);

        // A learned address that is also configured statically is shown once, as static.
        Assert.Equal([new MacEntryReading(49155, "00:11:22:00:00:03", 210, Static: true)],
            MacTableParser.Merge([new MacEntryReading(49155, "00:11:22:00:00:03", 210)], [new MacEntryReading(49155, "00:11:22:00:00:03", 210, Static: true)]));

        // BRIDGE-MIB static table (mac.receivePort -> PortList), invalid (2) rows skipped.
        Assert.Equal([new MacEntryReading(49154, "00:11:22:00:00:05", null, Static: true)],
            MacTableParser.ParseDot1dStatic(
                Rows(("0.17.34.0.0.5.0", PortList(0x40)), ("0.17.34.0.0.6.0", PortList(0x40))),
                Rows(("0.17.34.0.0.6.0", I(2))), basePorts));
    }

    [Fact]
    public void Dot1d_table_is_the_fallback_without_vlans_and_skips_unknown_ports()
    {
        var entries = MacTableParser.ParseDot1d(
            Rows(("0.17.34.51.68.85", I(3)), ("0.17.34.51.68.86", I(9))),
            Rows(("0.17.34.51.68.85", I(3)), ("0.17.34.51.68.86", I(3))),
            Rows(("3", I(3))));                                     // bridge port 9 has no ifIndex mapping
        Assert.Equal([new MacEntryReading(3, "00:11:22:33:44:55", null)], entries);
    }

    [Fact]
    public void Arp_table_gives_ip_per_mac_and_skips_invalid_and_broadcast_entries()
    {
        var arp = MacTableParser.ParseArp(
            Rows(("100.10.20.10.5", Mac(0x3C, 0x10, 0, 0x2A, 0, 5)), ("100.10.20.10.6", Mac(0x3C, 0x10, 0, 0x2A, 0, 6)), ("100.10.20.10.255", Mac(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF))),
            Rows(("100.10.20.10.6", I(2))));
        Assert.Equal([new ArpReading("10.20.10.5", "3C:10:00:2A:00:05", 100)], arp);
    }

    [Fact]
    public void Search_finds_a_mac_in_any_notation_or_by_ip_and_lists_the_access_port_first()
    {
        var store = new MacTableStore();
        var now = DateTime.UtcNow;
        // The PC is on access port Gi1/0/5 of the access switch and seen on the core's uplink together with 9 others.
        store.Update(1, "ACC-SW-01", now, [new("3C:10:00:2A:00:05", 10, 5, 105, "Gi1/0/5"), new("3C:10:00:2A:00:06", 20, 6, 106, "Gi1/0/6")], []);
        store.Update(2, "CORE-SW", now, Enumerable.Range(0, 10).Select(i => new MacPortEntry(i == 0 ? "3C:10:00:2A:00:05" : $"AA:00:00:00:00:{i:X2}", 10, 49, 249, "xe-0/0/49")).ToList(),
            [new ArpReading("10.20.10.5", "3C:10:00:2A:00:05", 100)]);

        foreach (var q in new[] { "3C:10:00:2A:00:05", "3c-10-00-2a-00-05", "3c10.002a.0005", "3C10002A0005", "2a:00:05", "10.20.10.5" })
        {
            var hits = store.Search(q);
            Assert.Equal(2, hits.Count);
            Assert.Equal(("ACC-SW-01", "Gi1/0/5", 1), (hits[0].DeviceName, hits[0].Port, hits[0].MacsOnPort));
            Assert.Equal(("CORE-SW", 10), (hits[1].DeviceName, hits[1].MacsOnPort));
            Assert.All(hits, h => Assert.Equal("10.20.10.5", h.Ip));
        }

        Assert.Empty(store.Search("zz:zz"));
        Assert.Empty(store.Search("3c"));         // too short to be meaningful
        store.Forget(1);
        Assert.Single(store.Search("3C:10:00:2A:00:05"));
        Assert.Null(store.Get(1));
    }

    [Fact]
    public void Tp_link_switches_are_detected_and_report_cpu_and_memory_of_the_busiest_unit()
    {
        Assert.Equal("TP-Link", VendorCatalog.VendorFromSysObjectId("1.3.6.1.4.1.11863.5.123"));
        Assert.Equal(NetWatch.Core.DeviceType.Switch, VendorCatalog.SuggestType("TP-Link", "JetStream 24-Port Gigabit L2+ Managed Switch"));

        // Two-unit stack: 1-minute CPU 12 % and 37 %, memory 41 % and 55 %.
        var (cpu, mem) = TpLinkHealthParser.Parse(Rows(("1", I(12)), ("2", I(37))), Rows(), Rows(("1", I(41)), ("2", I(55))));
        Assert.Equal((37.0, 55.0), (cpu, mem));

        // Firmware without the 1-minute column: the 5-second value is used; out-of-range values are ignored.
        (cpu, mem) = TpLinkHealthParser.Parse(Rows(), Rows(("1", I(8)), ("2", I(250))), Rows());
        Assert.Equal(8.0, cpu);
        Assert.Null(mem);
    }
}
