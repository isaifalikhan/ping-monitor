using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using NetWatch.Collectors.Snmp;

namespace NetWatch.Collectors.Processing;

/// <summary>A learned MAC address on one port, resolved to the NetWatch interface.</summary>
/// <summary>A MAC address on one port, resolved to the NetWatch interface. Static = configured on the switch.</summary>
public sealed record MacPortEntry(string Mac, int? Vlan, int IfIndex, int? InterfaceId, string Port, bool Static = false);

/// <summary>The MAC table of one switch as last read.</summary>
public sealed record DeviceMacTable(int DeviceId, string DeviceName, DateTime ReadUtc, IReadOnlyList<MacPortEntry> Entries)
{
    /// <summary>Number of MACs per ifIndex: one or two on an access port, many on an uplink or trunk.</summary>
    public IReadOnlyDictionary<int, int> MacsPerPort { get; } =
        Entries.GroupBy(e => e.IfIndex).ToDictionary(g => g.Key, g => g.Select(e => e.Mac).Distinct().Count());
}

/// <summary>Where a MAC address was found.</summary>
public sealed record MacLocation(int DeviceId, string DeviceName, string Port, int? InterfaceId, string Mac, int? Vlan, string? Ip, int MacsOnPort, DateTime ReadUtc, bool Static = false);

/// <summary>
/// Latest MAC address table per switch and the ARP entries seen on routed switches. Kept in memory: the tables are
/// re-read every <see cref="RefreshInterval"/>, so after a restart they are complete again within that time.
/// </summary>
public sealed class MacTableStore
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    /// <summary>Ports with more MACs than this are treated as uplinks/trunks when locating a device.</summary>
    public const int EdgePortMaxMacs = 4;

    private static readonly TimeSpan ArpMaxAge = TimeSpan.FromHours(4);
    private readonly ConcurrentDictionary<int, DeviceMacTable> _tables = new();
    private readonly ConcurrentDictionary<string, (string Ip, DateTime SeenUtc)> _ipByMac = new(StringComparer.Ordinal);

    public void Update(int deviceId, string deviceName, DateTime readUtc, IReadOnlyList<MacPortEntry> entries, IEnumerable<ArpReading> arp)
    {
        _tables[deviceId] = new DeviceMacTable(deviceId, deviceName, readUtc, entries);
        foreach (var a in arp)
        {
            _ipByMac[a.Mac] = (a.Ip, readUtc);
        }

        foreach (var (mac, (_, seen)) in _ipByMac)
        {
            if (readUtc - seen > ArpMaxAge)
            {
                _ipByMac.TryRemove(mac, out _);
            }
        }
    }

    public void Forget(int deviceId) => _tables.TryRemove(deviceId, out _);

    public DeviceMacTable? Get(int deviceId) => _tables.GetValueOrDefault(deviceId);

    public string? IpOf(string mac) => _ipByMac.TryGetValue(mac, out var v) ? v.Ip : null;

    /// <summary>
    /// Finds a MAC (any notation, or the last hex digits) or an IPv4 address (exact or prefix). Results are ordered so the
    /// access port the device is plugged into comes first: fewest MACs on the port, then newest table.
    /// </summary>
    public IReadOnlyList<MacLocation> Search(string query, int max = 200)
    {
        var q = query.Trim();
        var hex = NormalizeHex(q);
        var isIp = q.Count(c => c == '.') >= 1 && q.All(c => char.IsAsciiDigit(c) || c == '.');
        HashSet<string>? macsForIp = null;
        if (isIp)
        {
            macsForIp = _ipByMac.Where(kv => kv.Value.Ip == q || kv.Value.Ip.StartsWith(q.EndsWith('.') ? q : q + ".", StringComparison.Ordinal) || kv.Value.Ip == q)
                .Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        }
        else if (hex.Length < 4)
        {
            return [];
        }

        var result = new List<MacLocation>();
        foreach (var table in _tables.Values)
        {
            foreach (var e in table.Entries)
            {
                var match = macsForIp is not null ? macsForIp.Contains(e.Mac) : e.Mac.Replace(":", string.Empty, StringComparison.Ordinal).Contains(hex, StringComparison.Ordinal);
                if (match)
                {
                    result.Add(new MacLocation(table.DeviceId, table.DeviceName, e.Port, e.InterfaceId, e.Mac, e.Vlan, IpOf(e.Mac), table.MacsPerPort.GetValueOrDefault(e.IfIndex), table.ReadUtc, e.Static));
                }
            }
        }

        return result.OrderBy(r => r.Mac, StringComparer.Ordinal).ThenBy(r => r.MacsOnPort).ThenByDescending(r => r.ReadUtc).Take(max).ToList();
    }

    /// <summary>Upper-case hex digits of a MAC in any notation (AA:BB.., aa-bb.., aabb.ccdd.eeff, aabbccddeeff).</summary>
    public static string NormalizeHex(string text)
    {
        var sb = new StringBuilder(12);
        foreach (var c in text)
        {
            if (char.IsAsciiHexDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else if (c is not (':' or '-' or '.' or ' '))
            {
                return string.Empty;
            }
        }

        return sb.ToString();
    }

    internal static string PortLabel(int ifIndex) => "ifIndex " + ifIndex.ToString(CultureInfo.InvariantCulture);
}
