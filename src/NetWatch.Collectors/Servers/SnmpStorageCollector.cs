using NetWatch.Collectors.Snmp;

namespace NetWatch.Collectors.Servers;

/// <summary>
/// Disk volumes from HOST-RESOURCES-MIB hrStorageTable (fixed disks only). Used for servers monitored by SNMP
/// only (e.g. Linux with net-snmp, Windows with the SNMP service) when no WinRM/SSH credential is assigned.
/// </summary>
public static class SnmpStorageCollector
{
    private const string HrStorageFixedDisk = "1.3.6.1.2.1.25.2.1.4";
    private const string HrStorageDescr = "1.3.6.1.2.1.25.2.3.1.3";

    public static async Task<List<VolumeReading>> ReadAsync(SnmpClient client, CancellationToken ct)
    {
        var types = await client.WalkColumnAsync(Oids.HrStorageType, ct);
        var fixedDisks = types.Where(t => SnmpValues.Text(t.Value)?.TrimStart('.') == HrStorageFixedDisk).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var volumes = new List<VolumeReading>();
        if (fixedDisks.Count == 0)
        {
            return volumes;
        }

        var descr = await client.WalkColumnAsync(HrStorageDescr, ct);
        var units = await client.WalkColumnAsync(Oids.HrStorageAllocationUnits, ct);
        var size = await client.WalkColumnAsync(Oids.HrStorageSize, ct);
        var used = await client.WalkColumnAsync(Oids.HrStorageUsed, ct);

        foreach (var index in fixedDisks)
        {
            var unit = SnmpValues.Int64(units.GetValueOrDefault(index)) ?? 0;
            var total = (SnmpValues.Int64(size.GetValueOrDefault(index)) ?? 0) * unit;
            var usedBytes = (SnmpValues.Int64(used.GetValueOrDefault(index)) ?? 0) * unit;
            if (total <= 0)
            {
                continue;
            }

            // Windows reports "C:\ Label:System  Serial Number 1234"; keep the drive part as the name.
            var text = SnmpValues.Text(descr.GetValueOrDefault(index)) ?? $"Storage {index}";
            var name = text.Length >= 2 && text[1] == ':' ? text[..2] : text.Split(' ')[0];
            volumes.Add(new VolumeReading(name, null, null, total, Math.Max(0, total - usedBytes)));
        }

        return volumes;
    }
}
