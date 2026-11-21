using System.Globalization;
using System.Text.RegularExpressions;
using Lextm.SharpSnmpLib;
using NetWatch.Collectors.Polling;
using NetWatch.Core;

namespace NetWatch.Collectors.Snmp;

/// <summary>Reads system and ENTITY-MIB inventory: vendor, model, serial number, firmware.</summary>
public static partial class SystemInfoProbe
{
    public static async Task<DeviceInventoryInfo> ProbeAsync(SnmpClient client, CancellationToken ct)
    {
        var sys = await client.GetAsync([Oids.SysDescr, Oids.SysObjectId, Oids.SysName, Oids.SysLocation], ct);
        if (sys.Count == 0)
        {
            throw new SnmpException("Agent returned no system information.");
        }

        var descr = SnmpValues.Text(sys.GetValueOrDefault(Oids.SysDescr));
        var objectId = SnmpValues.Text(sys.GetValueOrDefault(Oids.SysObjectId))?.TrimStart('.');
        var vendor = VendorCatalog.VendorFromSysObjectId(objectId);

        string? model = null, serial = null, firmware = null;
        try
        {
            var classes = await client.WalkColumnAsync(Oids.EntPhysicalClass, ct);

            // entPhysicalClass 3 = chassis. Take the first chassis (stack master on stacked switches).
            var chassis = classes.Where(kv => SnmpValues.Int64(kv.Value) == 3)
                .Select(kv => kv.Key)
                .OrderBy(k => int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : int.MaxValue)
                .FirstOrDefault();
            if (chassis is not null)
            {
                var ent = await client.GetAsync(
                    [$"{Oids.EntPhysicalModelName}.{chassis}", $"{Oids.EntPhysicalSerialNum}.{chassis}", $"{Oids.EntPhysicalSoftwareRev}.{chassis}", $"{Oids.EntPhysicalFirmwareRev}.{chassis}", $"{Oids.EntPhysicalDescr}.{chassis}"],
                    ct);
                model = SnmpValues.Text(ent.GetValueOrDefault($"{Oids.EntPhysicalModelName}.{chassis}"))
                        ?? SnmpValues.Text(ent.GetValueOrDefault($"{Oids.EntPhysicalDescr}.{chassis}"));
                serial = SnmpValues.Text(ent.GetValueOrDefault($"{Oids.EntPhysicalSerialNum}.{chassis}"));
                firmware = SnmpValues.Text(ent.GetValueOrDefault($"{Oids.EntPhysicalSoftwareRev}.{chassis}"))
                           ?? SnmpValues.Text(ent.GetValueOrDefault($"{Oids.EntPhysicalFirmwareRev}.{chassis}"));
            }
        }
        catch (SnmpException)
        {
            // ENTITY-MIB is optional; fall back to sysDescr parsing below.
        }

        firmware ??= FirmwareFromDescription(descr);
        return new DeviceInventoryInfo(vendor, model, serial, firmware, objectId, SnmpValues.Text(sys.GetValueOrDefault(Oids.SysName)), descr,
            SnmpValues.Text(sys.GetValueOrDefault(Oids.SysLocation)));
    }

    /// <summary>Extracts a firmware version from common sysDescr formats (Cisco, HP/Aruba, Juniper).</summary>
    public static string? FirmwareFromDescription(string? descr)
    {
        if (string.IsNullOrEmpty(descr))
        {
            return null;
        }

        foreach (var re in new[] { CiscoVersion(), HpRevision(), JunosVersion() })
        {
            var m = re.Match(descr);
            if (m.Success)
            {
                return m.Groups[1].Value.TrimEnd(',', '.');
            }
        }

        return null;
    }

    [GeneratedRegex(@"Version\s+([0-9][^\s,]*)", RegexOptions.IgnoreCase)]
    private static partial Regex CiscoVersion();

    [GeneratedRegex(@"revision\s+([A-Z]{1,3}\.[0-9][^\s,]*)", RegexOptions.IgnoreCase)]
    private static partial Regex HpRevision();

    [GeneratedRegex(@"JUNOS\s+([0-9][^\s,\]]*)", RegexOptions.IgnoreCase)]
    private static partial Regex JunosVersion();
}

/// <summary>Reads the interface table (metadata) and interface counters.</summary>
public static class InterfaceCollector
{
    public static async Task<List<InterfaceMetadata>> ReadMetadataAsync(SnmpClient client, CancellationToken ct)
    {
        var descr = await client.WalkColumnAsync(Oids.IfDescr, ct);
        var types = await client.WalkColumnAsync(Oids.IfType, ct);
        var speed = await client.WalkColumnAsync(Oids.IfSpeed, ct);
        var mac = await client.WalkColumnAsync(Oids.IfPhysAddress, ct);
        var names = await SafeWalk(client, Oids.IfName, ct);
        var alias = await SafeWalk(client, Oids.IfAlias, ct);
        var highSpeed = await SafeWalk(client, Oids.IfHighSpeed, ct);

        var result = new List<InterfaceMetadata>(descr.Count);
        foreach (var (index, d) in descr)
        {
            if (!int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ifIndex))
            {
                continue;
            }

            var hs = SnmpValues.Int64(highSpeed.GetValueOrDefault(index)) ?? 0;
            var bps = hs > 0 ? hs * 1_000_000 : SnmpValues.Int64(speed.GetValueOrDefault(index)) ?? 0;
            var description = SnmpValues.Text(d);
            var name = SnmpValues.Text(names.GetValueOrDefault(index)) ?? description ?? $"if{ifIndex}";
            result.Add(new InterfaceMetadata(
                ifIndex,
                Truncate(name, 200)!,
                Truncate(description, 500),
                Truncate(SnmpValues.Text(alias.GetValueOrDefault(index)), 500),
                (int)(SnmpValues.Int64(types.GetValueOrDefault(index)) ?? 0),
                bps,
                SnmpValues.Mac(mac.GetValueOrDefault(index))));
        }

        return result;
    }

    public static async Task<List<InterfaceCounters>> ReadCountersAsync(SnmpClient client, CancellationToken ct)
    {
        var oper = await client.WalkColumnAsync(Oids.IfOperStatus, ct);
        var admin = await client.WalkColumnAsync(Oids.IfAdminStatus, ct);
        var hcIn = await SafeWalk(client, Oids.IfHcInOctets, ct);
        var hc = hcIn.Count > 0;
        var hcOut = hc ? await SafeWalk(client, Oids.IfHcOutOctets, ct) : new Dictionary<string, ISnmpData>();
        var inOctets = hc ? hcIn : await client.WalkColumnAsync(Oids.IfInOctets, ct);
        var outOctets = hc ? hcOut : await client.WalkColumnAsync(Oids.IfOutOctets, ct);
        var inErr = await SafeWalk(client, Oids.IfInErrors, ct);
        var outErr = await SafeWalk(client, Oids.IfOutErrors, ct);
        var inDisc = await SafeWalk(client, Oids.IfInDiscards, ct);
        var outDisc = await SafeWalk(client, Oids.IfOutDiscards, ct);
        var duplex = await SafeWalk(client, Oids.Dot3StatsDuplexStatus, ct);
        var lastChange = await SafeWalk(client, Oids.IfLastChange, ct);

        var result = new List<InterfaceCounters>(oper.Count);
        foreach (var (index, o) in oper)
        {
            if (!int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ifIndex))
            {
                continue;
            }

            result.Add(new InterfaceCounters(
                ifIndex,
                SnmpValues.UInt64(inOctets.GetValueOrDefault(index)),
                SnmpValues.UInt64(outOctets.GetValueOrDefault(index)),
                SnmpValues.UInt64(inErr.GetValueOrDefault(index)),
                SnmpValues.UInt64(outErr.GetValueOrDefault(index)),
                SnmpValues.UInt64(inDisc.GetValueOrDefault(index)),
                SnmpValues.UInt64(outDisc.GetValueOrDefault(index)),
                (int)(SnmpValues.Int64(o) ?? 4),
                (int)(SnmpValues.Int64(admin.GetValueOrDefault(index)) ?? 0),
                hc,
                (int?)SnmpValues.Int64(duplex.GetValueOrDefault(index)),
                SnmpValues.Int64(lastChange.GetValueOrDefault(index))));
        }

        return result;
    }

    /// <summary>Walks a column; an agent error (e.g. MIB not supported) returns an empty result instead of throwing.</summary>
    public static async Task<Dictionary<string, ISnmpData>> SafeWalk(SnmpClient client, string column, CancellationToken ct)
    {
        try
        {
            return await client.WalkColumnAsync(column, ct);
        }
        catch (SnmpException)
        {
            return new Dictionary<string, ISnmpData>();
        }
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}

/// <summary>CPU, memory and environmental sensors per vendor, with standard-MIB fallbacks.</summary>
public static class HealthCollector
{
    public sealed record Health(double? CpuPct, double? MemPct, List<SensorReading> Sensors);

    /// <param name="profile">Data-driven profile (built-in ArubaOS-CX or snmp-profiles.json); used instead of the vendor code.</param>
    public static async Task<Health> ReadAsync(SnmpClient client, string? vendor, CancellationToken ct, SnmpHealthProfile? profile = null)
    {
        // Generic profile for any other vendor: HOST-RESOURCES-MIB CPU/memory and ENTITY-SENSOR-MIB sensors below.
        Health health = profile is not null ? await ProfileHealthReader.ReadAsync(client, profile, ct) : vendor switch
        {
            "Cisco" => await CiscoAsync(client, ct),
            "HP" => await HpAsync(client, ct),
            "Juniper" => await JuniperAsync(client, ct),
            _ => new Health(null, null, new List<SensorReading>()),
        };

        // Standard MIB fallbacks for anything the vendor MIB did not provide.
        var cpu = health.CpuPct ?? await HostResourcesCpuAsync(client, ct);
        var mem = health.MemPct ?? await HostResourcesMemoryAsync(client, ct);
        var sensors = health.Sensors;
        if (!sensors.Any(s => s.Kind == SensorKind.Temperature))
        {
            sensors.AddRange(await EntitySensorTemperaturesAsync(client, ct));
        }

        return new Health(Round(cpu), Round(mem), sensors);
    }

    private static double? Round(double? v) => v is null ? null : Math.Round(Math.Clamp(v.Value, 0, 100), 1);

    // ---------------------------------------------------------------- Cisco

    private static async Task<Health> CiscoAsync(SnmpClient client, CancellationToken ct)
    {
        var cpuRows = await InterfaceCollector.SafeWalk(client, Oids.CiscoCpu5MinRev, ct);
        double? cpu = cpuRows.Count > 0 ? cpuRows.Values.Select(SnmpValues.Double).Where(v => v.HasValue).Average() : null;

        double? mem = null;
        var used = await InterfaceCollector.SafeWalk(client, Oids.CiscoCpuMemUsed, ct);
        var free = await InterfaceCollector.SafeWalk(client, Oids.CiscoCpuMemFree, ct);
        if (used.Count == 0)
        {
            used = await InterfaceCollector.SafeWalk(client, Oids.CiscoMemPoolUsed, ct);
            free = await InterfaceCollector.SafeWalk(client, Oids.CiscoMemPoolFree, ct);
        }

        mem = Percent(Sum(used), Sum(used) + Sum(free));

        var sensors = new List<SensorReading>();
        var tempDescr = await InterfaceCollector.SafeWalk(client, Oids.CiscoEnvTempDescr, ct);
        var tempValue = await InterfaceCollector.SafeWalk(client, Oids.CiscoEnvTempValue, ct);
        var tempState = await InterfaceCollector.SafeWalk(client, Oids.CiscoEnvTempState, ct);
        foreach (var (idx, d) in tempDescr)
        {
            sensors.Add(new SensorReading(SensorKind.Temperature, idx, SnmpValues.Text(d) ?? $"Temp {idx}",
                SnmpValues.Double(tempValue.GetValueOrDefault(idx)), "C", CiscoState(tempState.GetValueOrDefault(idx))));
        }

        await AddStatusSensors(client, Oids.CiscoEnvFanDescr, Oids.CiscoEnvFanState, SensorKind.Fan, CiscoState, sensors, ct);
        await AddStatusSensors(client, Oids.CiscoEnvPsuDescr, Oids.CiscoEnvPsuState, SensorKind.PowerSupply, CiscoState, sensors, ct);
        return new Health(cpu, mem, sensors);
    }

    /// <summary>CiscoEnvMonState: 1 normal, 2 warning, 3 critical, 4 shutdown, 5 notPresent, 6 notFunctioning.</summary>
    internal static SensorState CiscoState(ISnmpData? d) => SnmpValues.Int64(d) switch
    {
        1 => SensorState.Ok,
        2 => SensorState.Warning,
        3 or 4 or 6 => SensorState.Critical,
        5 => SensorState.NotPresent,
        _ => SensorState.Unknown,
    };

    // ---------------------------------------------------------------- HP / Aruba (ProCurve, ArubaOS-Switch)

    private static async Task<Health> HpAsync(SnmpClient client, CancellationToken ct)
    {
        double? cpu = null;
        try
        {
            var r = await client.GetAsync([Oids.HpCpu], ct);
            cpu = SnmpValues.Double(r.GetValueOrDefault(Oids.HpCpu));
        }
        catch (SnmpException)
        {
        }

        var total = await InterfaceCollector.SafeWalk(client, Oids.HpMemTotal, ct);
        var free = await InterfaceCollector.SafeWalk(client, Oids.HpMemFree, ct);
        var t = Sum(total);
        var mem = Percent(t - Sum(free), t);

        var sensors = new List<SensorReading>();
        var descr = await InterfaceCollector.SafeWalk(client, Oids.HpSensorDescr, ct);
        var status = await InterfaceCollector.SafeWalk(client, Oids.HpSensorStatus, ct);
        foreach (var (idx, d) in descr)
        {
            var name = SnmpValues.Text(d) ?? $"Sensor {idx}";
            var kind = name.Contains("fan", StringComparison.OrdinalIgnoreCase) ? SensorKind.Fan
                : name.Contains("power", StringComparison.OrdinalIgnoreCase) ? SensorKind.PowerSupply
                : name.Contains("temp", StringComparison.OrdinalIgnoreCase) ? SensorKind.Temperature
                : SensorKind.Other;
            sensors.Add(new SensorReading(kind, idx, name, null, null, HpState(status.GetValueOrDefault(idx))));
        }

        return new Health(cpu, mem, sensors);
    }

    /// <summary>hpicfSensorStatus: 1 unknown, 2 bad, 3 warning, 4 good, 5 notPresent.</summary>
    internal static SensorState HpState(ISnmpData? d) => SnmpValues.Int64(d) switch
    {
        2 => SensorState.Critical,
        3 => SensorState.Warning,
        4 => SensorState.Ok,
        5 => SensorState.NotPresent,
        _ => SensorState.Unknown,
    };

    // ---------------------------------------------------------------- Juniper

    private static async Task<Health> JuniperAsync(SnmpClient client, CancellationToken ct)
    {
        var descr = await InterfaceCollector.SafeWalk(client, Oids.JnxOperatingDescr, ct);
        if (descr.Count == 0)
        {
            return new Health(null, null, new List<SensorReading>());
        }

        var state = await InterfaceCollector.SafeWalk(client, Oids.JnxOperatingState, ct);
        var temp = await InterfaceCollector.SafeWalk(client, Oids.JnxOperatingTemp, ct);
        var cpu = await InterfaceCollector.SafeWalk(client, Oids.JnxOperatingCpu, ct);
        var buffer = await InterfaceCollector.SafeWalk(client, Oids.JnxOperatingBuffer, ct);

        var rows = descr.Select(kv => new
        {
            Index = kv.Key,
            Name = SnmpValues.Text(kv.Value) ?? kv.Key,
            State = SnmpValues.Int64(state.GetValueOrDefault(kv.Key)),
            Temp = SnmpValues.Double(temp.GetValueOrDefault(kv.Key)),
            Cpu = SnmpValues.Double(cpu.GetValueOrDefault(kv.Key)),
            Buffer = SnmpValues.Double(buffer.GetValueOrDefault(kv.Key)),
        }).ToList();

        // Routing Engine rows carry the control-plane CPU and memory; on EX switches FPC rows do too.
        var re = rows.Where(r => r.Name.Contains("Routing Engine", StringComparison.OrdinalIgnoreCase)).ToList();
        var source = re.Count > 0 ? re : rows.Where(r => r.Cpu > 0 || r.Buffer > 0).ToList();
        double? cpuPct = source.Count > 0 ? source.Max(r => r.Cpu ?? 0) : null;
        double? memPct = source.Count > 0 ? source.Max(r => r.Buffer ?? 0) : null;

        var sensors = new List<SensorReading>();
        foreach (var r in rows)
        {
            var name = r.Name;
            if (name.Contains("Fan", StringComparison.OrdinalIgnoreCase))
            {
                sensors.Add(new SensorReading(SensorKind.Fan, r.Index, name, null, null, JuniperState(r.State)));
            }
            else if (name.Contains("Power", StringComparison.OrdinalIgnoreCase) || name.Contains("PEM", StringComparison.Ordinal) || name.Contains("PSU", StringComparison.Ordinal))
            {
                sensors.Add(new SensorReading(SensorKind.PowerSupply, r.Index, name, null, null, JuniperState(r.State)));
            }
            else if (r.Temp > 0 && sensors.Count(s => s.Kind == SensorKind.Temperature) < 12)
            {
                sensors.Add(new SensorReading(SensorKind.Temperature, r.Index, name, r.Temp, "C", SensorState.Ok));
            }
        }

        return new Health(cpuPct, memPct, sensors);
    }

    /// <summary>jnxOperatingState: 1 unknown, 2 running, 3 ready, 4 reset, 5 runningAtFullSpeed, 6 down, 7 standby.</summary>
    internal static SensorState JuniperState(long? state) => state switch
    {
        2 or 3 or 7 => SensorState.Ok,
        5 => SensorState.Warning,
        4 or 6 => SensorState.Critical,
        _ => SensorState.Unknown,
    };

    // ---------------------------------------------------------------- standard MIBs

    private static async Task<double?> HostResourcesCpuAsync(SnmpClient client, CancellationToken ct)
    {
        var loads = await InterfaceCollector.SafeWalk(client, Oids.HrProcessorLoad, ct);
        var values = loads.Values.Select(SnmpValues.Double).Where(v => v.HasValue).ToList();
        return values.Count > 0 ? values.Average() : null;
    }

    private static async Task<double?> HostResourcesMemoryAsync(SnmpClient client, CancellationToken ct)
    {
        var types = await InterfaceCollector.SafeWalk(client, Oids.HrStorageType, ct);
        var ram = types.Where(kv => SnmpValues.Text(kv.Value)?.TrimStart('.') == Oids.HrStorageRam).Select(kv => kv.Key).FirstOrDefault();
        if (ram is null)
        {
            return null;
        }

        var r = await client.GetAsync([$"{Oids.HrStorageSize}.{ram}", $"{Oids.HrStorageUsed}.{ram}"], ct);
        var size = SnmpValues.Double(r.GetValueOrDefault($"{Oids.HrStorageSize}.{ram}"));
        var used = SnmpValues.Double(r.GetValueOrDefault($"{Oids.HrStorageUsed}.{ram}"));
        return size is > 0 && used.HasValue ? used / size * 100 : null;
    }

    private static async Task<List<SensorReading>> EntitySensorTemperaturesAsync(SnmpClient client, CancellationToken ct)
    {
        var result = new List<SensorReading>();
        var types = await InterfaceCollector.SafeWalk(client, Oids.EntPhySensorType, ct);
        var celsius = types.Where(kv => SnmpValues.Int64(kv.Value) == 8).Select(kv => kv.Key).Take(12).ToList();
        if (celsius.Count == 0)
        {
            return result;
        }

        var values = await InterfaceCollector.SafeWalk(client, Oids.EntPhySensorValue, ct);
        var scales = await InterfaceCollector.SafeWalk(client, Oids.EntPhySensorScale, ct);
        var precisions = await InterfaceCollector.SafeWalk(client, Oids.EntPhySensorPrecision, ct);
        var status = await InterfaceCollector.SafeWalk(client, Oids.EntPhySensorOperStatus, ct);
        var names = await InterfaceCollector.SafeWalk(client, Oids.EntPhysicalName, ct);

        foreach (var idx in celsius)
        {
            var raw = SnmpValues.Double(values.GetValueOrDefault(idx));
            if (raw is null)
            {
                continue;
            }

            var value = EntitySensorValue(raw.Value, (int)(SnmpValues.Int64(scales.GetValueOrDefault(idx)) ?? 9), (int)(SnmpValues.Int64(precisions.GetValueOrDefault(idx)) ?? 0));
            var state = SnmpValues.Int64(status.GetValueOrDefault(idx)) switch
            {
                1 => SensorState.Ok,
                3 => SensorState.Critical,
                _ => SensorState.Unknown,
            };
            result.Add(new SensorReading(SensorKind.Temperature, $"ent{idx}", SnmpValues.Text(names.GetValueOrDefault(idx)) ?? $"Sensor {idx}", Math.Round(value, 1), "C", state));
        }

        return result;
    }

    /// <summary>ENTITY-SENSOR-MIB value with SensorDataScale (9 = units, 8 = milli, 10 = kilo …) and precision.</summary>
    public static double EntitySensorValue(double raw, int scale, int precision)
    {
        var exponent = (scale - 9) * 3;
        return raw * Math.Pow(10, exponent) / Math.Pow(10, Math.Clamp(precision, -8, 9));
    }

    private static async Task AddStatusSensors(SnmpClient client, string descrOid, string stateOid, SensorKind kind, Func<ISnmpData?, SensorState> map, List<SensorReading> sensors, CancellationToken ct)
    {
        var descr = await InterfaceCollector.SafeWalk(client, descrOid, ct);
        if (descr.Count == 0)
        {
            return;
        }

        var states = await InterfaceCollector.SafeWalk(client, stateOid, ct);
        foreach (var (idx, d) in descr)
        {
            var state = map(states.GetValueOrDefault(idx));
            if (state != SensorState.NotPresent)
            {
                sensors.Add(new SensorReading(kind, idx, SnmpValues.Text(d) ?? $"{kind} {idx}", null, null, state));
            }
        }
    }

    private static double Sum(Dictionary<string, ISnmpData> rows) => rows.Values.Select(SnmpValues.Double).Where(v => v.HasValue).Sum(v => v!.Value);

    private static double? Percent(double used, double total) => total > 0 ? used / total * 100 : null;
}
