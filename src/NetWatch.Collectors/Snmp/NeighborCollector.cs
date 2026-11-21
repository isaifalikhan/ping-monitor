using System.Globalization;
using System.Net;
using Lextm.SharpSnmpLib;

namespace NetWatch.Collectors.Snmp;

/// <summary>
/// One neighbour seen on a local port through LLDP or CDP.
/// <paramref name="LocalPortNum"/> is the LLDP local port number or the CDP ifIndex; <paramref name="LocalIfIndex"/>
/// is set when it is known to be an ifIndex (CDP) and resolved later for LLDP by name.
/// </summary>
public sealed record NeighborReading(
    string Protocol,
    int LocalPortNum,
    int? LocalIfIndex,
    string? LocalPortId,
    string? LocalPortDesc,
    string? RemoteName,
    string? RemoteChassisId,
    string? RemotePort,
    string? RemotePortDesc,
    string? RemoteAddress,
    string? RemotePlatform);

/// <summary>Reads LLDP-MIB (IEEE 802.1AB) and CISCO-CDP-MIB neighbour tables.</summary>
public static class NeighborCollector
{
    public static async Task<List<NeighborReading>> ReadAsync(SnmpClient client, CancellationToken ct)
    {
        async Task<Dictionary<string, ISnmpData>> W(string oid) => await InterfaceCollector.SafeWalk(client, oid, ct);

        var result = new List<NeighborReading>();
        var sysName = await W(Oids.LldpRemSysName);
        var chassis = await W(Oids.LldpRemChassisId);
        if (sysName.Count > 0 || chassis.Count > 0)
        {
            result.AddRange(NeighborParser.ParseLldp(
                await W(Oids.LldpRemChassisIdSubtype), chassis, await W(Oids.LldpRemPortIdSubtype), await W(Oids.LldpRemPortId), await W(Oids.LldpRemPortDesc),
                sysName, await W(Oids.LldpRemSysDesc), await W(Oids.LldpLocPortId), await W(Oids.LldpLocPortDesc), await W(Oids.LldpRemManAddrIfSubtype)));
        }

        var cdpId = await W(Oids.CdpCacheDeviceId);
        if (cdpId.Count > 0)
        {
            result.AddRange(NeighborParser.ParseCdp(cdpId, await W(Oids.CdpCacheDevicePort), await W(Oids.CdpCacheAddressType), await W(Oids.CdpCacheAddress), await W(Oids.CdpCachePlatform)));
        }

        return NeighborParser.Deduplicate(result);
    }
}

/// <summary>Pure parsers for the LLDP and CDP tables (unit tested with snmpwalk samples).</summary>
public static class NeighborParser
{
    /// <summary>
    /// LLDP remote table rows are indexed by "timeMark.localPortNum.remIndex"; the local port table by
    /// "localPortNum"; the management address table by "timeMark.localPortNum.remIndex.subtype.length.address…".
    /// </summary>
    public static List<NeighborReading> ParseLldp(
        IReadOnlyDictionary<string, ISnmpData> chassisSubtype,
        IReadOnlyDictionary<string, ISnmpData> chassisId,
        IReadOnlyDictionary<string, ISnmpData> portSubtype,
        IReadOnlyDictionary<string, ISnmpData> portId,
        IReadOnlyDictionary<string, ISnmpData> portDesc,
        IReadOnlyDictionary<string, ISnmpData> sysName,
        IReadOnlyDictionary<string, ISnmpData> sysDesc,
        IReadOnlyDictionary<string, ISnmpData> localPortId,
        IReadOnlyDictionary<string, ISnmpData> localPortDesc,
        IReadOnlyDictionary<string, ISnmpData> managementAddresses)
    {
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in managementAddresses.Keys)
        {
            // timeMark.port.rem.subtype(1 = IPv4).length(4).a.b.c.d
            var parts = key.Split('.');
            if (parts.Length >= 9 && parts[3] == "1" && parts[4] == "4")
            {
                addresses.TryAdd(string.Join('.', parts[..3]), string.Join('.', parts[5..9]));
            }
        }

        var keys = chassisId.Keys.Concat(sysName.Keys).Distinct(StringComparer.Ordinal);
        var result = new List<NeighborReading>();
        foreach (var key in keys)
        {
            var parts = key.Split('.');
            if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var localPort))
            {
                continue;
            }

            var local = parts[1];
            var chassisValue = Identifier(chassisSubtype.GetValueOrDefault(key), chassisId.GetValueOrDefault(key), macSubtype: 4, addressSubtype: 5);
            var portValue = Identifier(portSubtype.GetValueOrDefault(key), portId.GetValueOrDefault(key), macSubtype: 3, addressSubtype: 4);
            var name = SnmpValues.Text(sysName.GetValueOrDefault(key));
            result.Add(new NeighborReading(
                "LLDP",
                localPort,
                null,
                SnmpValues.Text(localPortId.GetValueOrDefault(local)),
                SnmpValues.Text(localPortDesc.GetValueOrDefault(local)),
                Clip(name, 255),
                Clip(chassisValue, 100),
                Clip(portValue, 255),
                Clip(SnmpValues.Text(portDesc.GetValueOrDefault(key)), 255),
                addresses.GetValueOrDefault(key),
                Clip(FirstLine(SnmpValues.Text(sysDesc.GetValueOrDefault(key))), 255)));
        }

        return result;
    }

    /// <summary>CDP cache rows are indexed by "ifIndex.deviceIndex".</summary>
    public static List<NeighborReading> ParseCdp(
        IReadOnlyDictionary<string, ISnmpData> deviceId,
        IReadOnlyDictionary<string, ISnmpData> devicePort,
        IReadOnlyDictionary<string, ISnmpData> addressType,
        IReadOnlyDictionary<string, ISnmpData> address,
        IReadOnlyDictionary<string, ISnmpData> platform)
    {
        var result = new List<NeighborReading>();
        foreach (var (key, id) in deviceId)
        {
            var parts = key.Split('.');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ifIndex))
            {
                continue;
            }

            string? ip = null;
            if (SnmpValues.Int64(addressType.GetValueOrDefault(key)) is null or 1 && address.GetValueOrDefault(key) is OctetString raw && raw.GetRaw() is { Length: 4 } bytes)
            {
                ip = new IPAddress(bytes).ToString();
            }

            // CDP device IDs are often "switch.corp.local(SERIAL)" on NX-OS: keep the host part.
            var name = SnmpValues.Text(id);
            if (name is not null && name.IndexOf('(') is > 0 and var paren)
            {
                name = name[..paren];
            }

            result.Add(new NeighborReading("CDP", ifIndex, ifIndex, null, null, Clip(name, 255), null,
                Clip(SnmpValues.Text(devicePort.GetValueOrDefault(key)), 255), null, ip, Clip(SnmpValues.Text(platform.GetValueOrDefault(key)), 255)));
        }

        return result;
    }

    /// <summary>A Cisco device often reports the same neighbour by LLDP and CDP; keep one row per local port and neighbour.</summary>
    public static List<NeighborReading> Deduplicate(List<NeighborReading> neighbors) =>
        neighbors
            .GroupBy(n => (n.LocalIfIndex?.ToString(CultureInfo.InvariantCulture) ?? n.LocalPortId ?? n.LocalPortNum.ToString(CultureInfo.InvariantCulture),
                ShortName(n.RemoteName) ?? n.RemoteChassisId ?? n.RemoteAddress ?? string.Empty))
            .Select(g => g.OrderBy(n => n.Protocol == "LLDP" ? 0 : 1).First() with
            {
                RemoteAddress = g.Select(n => n.RemoteAddress).FirstOrDefault(a => a is not null),
                RemotePlatform = g.Select(n => n.RemotePlatform).FirstOrDefault(a => a is not null),
                LocalIfIndex = g.Select(n => n.LocalIfIndex).FirstOrDefault(i => i is not null),
            })
            .ToList();

    /// <summary>
    /// Resolves the LLDP local port number to an ifIndex: by lldpLocPortId/Desc matching ifName/ifDescr,
    /// otherwise the port number itself when such an ifIndex exists (common on Cisco and HP).
    /// </summary>
    public static int? ResolveLocalIfIndex(NeighborReading n, IReadOnlyDictionary<int, (string Name, string? Description)> interfaces)
    {
        if (n.LocalIfIndex is { } known)
        {
            return known;
        }

        foreach (var candidate in new[] { n.LocalPortId, n.LocalPortDesc })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            foreach (var (index, i) in interfaces)
            {
                if (string.Equals(i.Name, candidate, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Description, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        return interfaces.ContainsKey(n.LocalPortNum) ? n.LocalPortNum : null;
    }

    public static string? ShortName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim().Split('.')[0].ToUpperInvariant();

    private static string? Identifier(ISnmpData? subtype, ISnmpData? value, int macSubtype, int addressSubtype)
    {
        var type = SnmpValues.Int64(subtype);
        if (type == macSubtype && SnmpValues.Mac(value) is { } mac)
        {
            return mac;
        }

        if (type == addressSubtype && value is OctetString s && s.GetRaw() is { Length: 5 } raw && raw[0] == 1)
        {
            return new IPAddress(raw[1..]).ToString(); // IANA address family 1 + IPv4
        }

        return SnmpValues.Text(value);
    }

    private static string? FirstLine(string? text) => text?.Split('\n', '\r')[0].Trim();

    private static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
