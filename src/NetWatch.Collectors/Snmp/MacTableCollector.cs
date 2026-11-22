using System.Globalization;
using Lextm.SharpSnmpLib;

namespace NetWatch.Collectors.Snmp;

/// <summary>One MAC address the switch has learned on a port. <paramref name="Vlan"/> is null when the switch only has
/// the VLAN-less BRIDGE-MIB table.</summary>
public sealed record MacEntryReading(int IfIndex, string Mac, int? Vlan);

/// <summary>One ARP entry (IPv4 -> MAC) from a routed switch.</summary>
public sealed record ArpReading(string Ip, string Mac, int IfIndex);

/// <summary>The MAC address table and the ARP table of one device, read together.</summary>
public sealed record MacTableReading(List<MacEntryReading> Entries, List<ArpReading> Arp);

/// <summary>
/// Reads the learned MAC addresses per port (Q-BRIDGE-MIB with VLANs, falling back to BRIDGE-MIB) and the ARP table
/// (IP-MIB). Standard MIBs only, so it works on TP-Link, Juniper, HP/Aruba and most other managed switches. Cisco IOS
/// returns the BRIDGE-MIB table per VLAN only (community@vlan / v3 context); there it shows the default VLAN.
/// </summary>
public static class MacTableCollector
{
    public static async Task<MacTableReading> ReadAsync(SnmpClient client, CancellationToken ct)
    {
        async Task<Dictionary<string, ISnmpData>> W(string oid) => await InterfaceCollector.SafeWalk(client, oid, ct);

        var basePorts = await W(Oids.Dot1dBasePortIfIndex);
        var entries = new List<MacEntryReading>();
        var qPorts = await W(Oids.Dot1qTpFdbPort);
        if (qPorts.Count > 0)
        {
            entries = MacTableParser.ParseQBridge(qPorts, await W(Oids.Dot1qTpFdbStatus), await W(Oids.Dot1qVlanFdbId), basePorts);
        }

        // No Q-BRIDGE table, or none of its ports could be resolved: use the VLAN-less BRIDGE-MIB table.
        if (entries.Count == 0)
        {
            var dPorts = await W(Oids.Dot1dTpFdbPort);
            if (dPorts.Count > 0)
            {
                entries = MacTableParser.ParseDot1d(dPorts, await W(Oids.Dot1dTpFdbStatus), basePorts);
            }
        }

        var arp = MacTableParser.ParseArp(await W(Oids.IpNetToMediaPhysAddress), await W(Oids.IpNetToMediaType));
        return new MacTableReading(entries, arp);
    }
}

/// <summary>Pure parsers for the MAC and ARP tables (unit tested with constructed SNMP rows).</summary>
public static class MacTableParser
{
    /// <summary>Rows learned dynamically (3) or of unknown origin (1, "other"); self (4), mgmt (5) and invalid (2) are skipped.</summary>
    private static bool IsLearned(ISnmpData? status) => SnmpValues.Int64(status) is null or 1 or 3;

    /// <summary>dot1qTpFdbPort is indexed by "fdbId.m1.m2.m3.m4.m5.m6". dot1qVlanFdbId ("timeMark.vlanId" -> fdbId) turns
    /// the FDB id into the VLAN id where they differ (e.g. Juniper); without it the FDB id is the VLAN id (TP-Link, HP).</summary>
    public static List<MacEntryReading> ParseQBridge(
        IReadOnlyDictionary<string, ISnmpData> fdbPort,
        IReadOnlyDictionary<string, ISnmpData> fdbStatus,
        IReadOnlyDictionary<string, ISnmpData> vlanFdbId,
        IReadOnlyDictionary<string, ISnmpData> basePortIfIndex)
    {
        var vlanOfFdb = new Dictionary<long, int>();
        foreach (var (key, value) in vlanFdbId)
        {
            var parts = key.Split('.');
            if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var vlan) && SnmpValues.Int64(value) is { } fdb)
            {
                vlanOfFdb.TryAdd(fdb, vlan);
            }
        }

        var result = new List<MacEntryReading>();
        foreach (var (key, value) in fdbPort)
        {
            var parts = key.Split('.');
            if (parts.Length != 7 || !IsLearned(fdbStatus.GetValueOrDefault(key)) || MacFromIndex(parts.AsSpan(1)) is not { } mac ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var fdbId) ||
                ResolveIfIndex(value, basePortIfIndex) is not { } ifIndex)
            {
                continue;
            }

            result.Add(new MacEntryReading(ifIndex, mac, vlanOfFdb.TryGetValue(fdbId, out var vlan) ? vlan : (int)fdbId));
        }

        return Distinct(result);
    }

    /// <summary>dot1dTpFdbPort is indexed by the six MAC octets.</summary>
    public static List<MacEntryReading> ParseDot1d(
        IReadOnlyDictionary<string, ISnmpData> fdbPort,
        IReadOnlyDictionary<string, ISnmpData> fdbStatus,
        IReadOnlyDictionary<string, ISnmpData> basePortIfIndex)
    {
        var result = new List<MacEntryReading>();
        foreach (var (key, value) in fdbPort)
        {
            var parts = key.Split('.');
            if (parts.Length != 6 || !IsLearned(fdbStatus.GetValueOrDefault(key)) || MacFromIndex(parts) is not { } mac ||
                ResolveIfIndex(value, basePortIfIndex) is not { } ifIndex)
            {
                continue;
            }

            result.Add(new MacEntryReading(ifIndex, mac, null));
        }

        return Distinct(result);
    }

    /// <summary>ipNetToMediaPhysAddress is indexed by "ifIndex.a.b.c.d"; ipNetToMediaType 2 = invalid.</summary>
    public static List<ArpReading> ParseArp(IReadOnlyDictionary<string, ISnmpData> physAddress, IReadOnlyDictionary<string, ISnmpData> type)
    {
        var result = new List<ArpReading>();
        foreach (var (key, value) in physAddress)
        {
            var parts = key.Split('.');
            if (parts.Length != 5 || SnmpValues.Int64(type.GetValueOrDefault(key)) == 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ifIndex) ||
                SnmpValues.Mac(value) is not { } mac || mac == "00:00:00:00:00:00" || mac == "FF:FF:FF:FF:FF:FF" ||
                !parts.Skip(1).All(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                continue;
            }

            result.Add(new ArpReading(string.Join('.', parts.Skip(1)), mac, ifIndex));
        }

        return result;
    }

    /// <summary>"AA:BB:CC:DD:EE:FF" from six decimal index components; null for anything else or the zero address.</summary>
    public static string? MacFromIndex(ReadOnlySpan<string> octets)
    {
        if (octets.Length != 6)
        {
            return null;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(octets[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }

        return bytes.All(b => b == 0) ? null : string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    /// <summary>Bridge port -> ifIndex through dot1dBasePortIfIndex. Switches without that table number bridge ports as ifIndexes.</summary>
    private static int? ResolveIfIndex(ISnmpData bridgePortValue, IReadOnlyDictionary<string, ISnmpData> basePortIfIndex)
    {
        if (SnmpValues.Int64(bridgePortValue) is not { } port || port <= 0 || port > int.MaxValue)
        {
            return null; // 0 = the switch itself / unknown port
        }

        if (basePortIfIndex.Count == 0)
        {
            return (int)port;
        }

        if (SnmpValues.Int64(basePortIfIndex.GetValueOrDefault(port.ToString(CultureInfo.InvariantCulture))) is { } ifIndex and > 0 and <= int.MaxValue)
        {
            return (int)ifIndex;
        }

        // TP-Link Omada/JetStream: dot1dBasePort is 49153, 49154 … but dot1qTpFdbPort gives the port's position 1, 2 …
        // (e.g. 25 = the 25th bridge port = 49177). Only used when the value is not a bridge port at all.
        if (port <= basePortIfIndex.Count)
        {
            var nth = basePortIfIndex
                .Select(kv => (Port: long.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : -1, IfIndex: SnmpValues.Int64(kv.Value)))
                .Where(x => x.Port > 0)
                .OrderBy(x => x.Port)
                .ElementAtOrDefault((int)port - 1);
            if (nth.IfIndex is { } positional and > 0 and <= int.MaxValue)
            {
                return (int)positional;
            }
        }

        return null;
    }

    private static List<MacEntryReading> Distinct(List<MacEntryReading> entries) =>
        entries.DistinctBy(e => (e.IfIndex, e.Mac, e.Vlan)).OrderBy(e => e.IfIndex).ThenBy(e => e.Vlan).ThenBy(e => e.Mac, StringComparer.Ordinal).ToList();
}

/// <summary>CPU and memory of TP-Link JetStream / Omada switches (TPLINK-SYSMONITOR-MIB), busiest unit of a stack.</summary>
public static class TpLinkHealthParser
{
    public static (double? CpuPct, double? MemPct) Parse(
        IReadOnlyDictionary<string, ISnmpData> cpu1Minute,
        IReadOnlyDictionary<string, ISnmpData> cpu5Seconds,
        IReadOnlyDictionary<string, ISnmpData> memoryUtilization)
    {
        static double? Max(IReadOnlyDictionary<string, ISnmpData> column)
        {
            var values = column.Values.Select(SnmpValues.Double).Where(v => v is >= 0 and <= 100).Select(v => v!.Value).ToList();
            return values.Count > 0 ? values.Max() : null;
        }

        return (Max(cpu1Minute) ?? Max(cpu5Seconds), Max(memoryUtilization));
    }
}
