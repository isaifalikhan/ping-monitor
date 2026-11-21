using System.Globalization;
using System.Text.Json;

namespace NetWatch.Collectors.Hardware;

/// <summary>One hardware component (Redfish resource or SNMP status) of a server.</summary>
public sealed record HardwareReading(string Category, string Name, string? Health, string? State = null, double? Reading = null, string? Unit = null, string? Details = null)
{
    /// <summary>Present and reported Warning or Critical.</summary>
    public bool IsProblem =>
        !string.Equals(State, "Absent", StringComparison.OrdinalIgnoreCase) &&
        Health is not null && !Health.Equals("OK", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the hardware collector produced for one server.</summary>
public sealed class HardwarePollData
{
    /// <summary>"Redfish" or "SNMP".</summary>
    public string Source { get; set; } = "Redfish";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<HardwareReading> Components { get; } = new();

    /// <summary>Total power consumption (W) when reported.</summary>
    public double? PowerWatts { get; set; }

    /// <summary>Certificate accepted on first use, to be pinned on the device.</summary>
    public string? NewTlsThumbprint { get; set; }
}

/// <summary>
/// Pure parsers for the DMTF Redfish resources iLO 5/6 and iDRAC 9 expose (ComputerSystem, Thermal, Power,
/// Storage, Drive). Unit tested with HPE and Dell sample documents. Unknown or missing properties are skipped.
/// </summary>
public static class RedfishParser
{
    public static IEnumerable<HardwareReading> System(JsonElement system)
    {
        var details = Join(Str(system, "Manufacturer"), Str(system, "Model"), Str(system, "SerialNumber") is { } sn ? $"S/N {sn}" : null,
            Str(system, "BiosVersion") is { } bios ? $"BIOS {bios}" : null, Str(system, "PowerState") is { } ps ? $"power {ps}" : null);
        yield return new HardwareReading("System", Str(system, "HostName") is { Length: > 0 } h ? h : Str(system, "Model") ?? "System",
            Rollup(system), State(system), Details: details);

        if (system.TryGetProperty("ProcessorSummary", out var cpu) && cpu.ValueKind == JsonValueKind.Object)
        {
            yield return new HardwareReading("Processor", "Processors", Rollup(cpu), State(cpu), Num(cpu, "Count"), "CPU", Str(cpu, "Model"));
        }

        if (system.TryGetProperty("MemorySummary", out var mem) && mem.ValueKind == JsonValueKind.Object)
        {
            yield return new HardwareReading("Memory", "Memory", Rollup(mem), State(mem), Num(mem, "TotalSystemMemoryGiB"), "GiB");
        }
    }

    /// <summary>Chassis/{id}/Thermal (classic schema, iLO 5 and iDRAC 9).</summary>
    public static IEnumerable<HardwareReading> Thermal(JsonElement thermal)
    {
        foreach (var t in Array(thermal, "Temperatures"))
        {
            if (State(t) is "Absent")
            {
                continue;
            }

            var reading = Num(t, "ReadingCelsius");
            var critical = Num(t, "UpperThresholdCritical") ?? Num(t, "UpperThresholdFatal");
            yield return new HardwareReading("Temperature", Str(t, "Name") ?? Str(t, "MemberId") ?? "Sensor", Health(t), State(t), reading, "C",
                critical is { } c && c > 0 ? $"critical at {c.ToString("0", CultureInfo.InvariantCulture)} °C" : null);
        }

        foreach (var f in Array(thermal, "Fans"))
        {
            if (State(f) is "Absent")
            {
                continue;
            }

            var unit = Str(f, "ReadingUnits") switch
            {
                "RPM" => "RPM",
                "Percent" => "%",
                _ => null,
            };
            yield return new HardwareReading("Fan", Str(f, "Name") ?? Str(f, "FanName") ?? Str(f, "MemberId") ?? "Fan", Health(f), State(f), Num(f, "Reading"), unit);
        }
    }

    /// <summary>Chassis/{id}/Power (classic schema). Returns the readings and the consumed watts.</summary>
    public static (List<HardwareReading> Readings, double? Watts) Power(JsonElement power)
    {
        var list = new List<HardwareReading>();
        foreach (var p in Array(power, "PowerSupplies"))
        {
            if (State(p) is "Absent")
            {
                continue;
            }

            list.Add(new HardwareReading("PowerSupply", Str(p, "Name") ?? Str(p, "MemberId") ?? "Power supply", Health(p), State(p),
                Num(p, "LastPowerOutputWatts") ?? Num(p, "PowerOutputWatts"), "W",
                Join(Str(p, "Model"), Num(p, "PowerCapacityWatts") is { } cap ? $"{cap:0} W" : null, Str(p, "FirmwareVersion") is { } fw ? $"FW {fw}" : null)));
        }

        double? watts = Array(power, "PowerControl").Select(c => Num(c, "PowerConsumedWatts")).FirstOrDefault(w => w is not null);
        return (list, watts);
    }

    /// <summary>Systems/{id}/Storage/{id} (controllers inline) plus its drives and volumes (already fetched).</summary>
    public static IEnumerable<HardwareReading> Storage(JsonElement storage, IEnumerable<JsonElement> drives)
    {
        var name = Str(storage, "Name") ?? Str(storage, "Id") ?? "Storage";
        var controllers = Array(storage, "StorageControllers").ToList();
        if (controllers.Count == 0)
        {
            yield return new HardwareReading("StorageController", name, Rollup(storage), State(storage));
        }

        foreach (var c in controllers)
        {
            yield return new HardwareReading("StorageController", Str(c, "Name") ?? Str(c, "Model") ?? name, Health(c), State(c),
                Details: Join(Str(c, "Model"), Str(c, "FirmwareVersion") is { } fw ? $"FW {fw}" : null));
        }

        foreach (var d in drives)
        {
            if (State(d) is "Absent")
            {
                continue;
            }

            double? gb = Num(d, "CapacityBytes") is { } bytes ? Math.Round(bytes / 1e9) : null;
            yield return new HardwareReading("Drive", Str(d, "Name") ?? Str(d, "Id") ?? "Drive", Health(d), State(d), gb, "GB",
                Join(Str(d, "Model"), Str(d, "MediaType"), Str(d, "SerialNumber") is { } sn ? $"S/N {sn}" : null,
                    Num(d, "PredictedMediaLifeLeftPercent") is { } life ? $"{life:0} % life left" : null, Str(d, "FailurePredicted") == "True" ? "failure predicted" : null));
        }
    }

    /// <summary>The @odata.id links of a collection ("Members") or of a link property.</summary>
    public static List<string> Links(JsonElement element, string property = "Members") =>
        Array(element, property).Select(m => Str(m, "@odata.id")).Where(id => id is not null).Select(id => id!).ToList();

    /// <summary>The @odata.id of an object property such as "Thermal" or "Storage".</summary>
    public static string? Link(JsonElement element, string property) =>
        element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "@odata.id") : null;

    private static IEnumerable<JsonElement> Array(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s.Trim() : null,
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                _ => null,
            }
            : null;

    private static double? Num(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static string? Health(JsonElement e) =>
        e.TryGetProperty("Status", out var s) && s.ValueKind == JsonValueKind.Object ? Str(s, "Health") : null;

    /// <summary>HealthRollup (worst of the resource and its children) when present, else Health.</summary>
    private static string? Rollup(JsonElement e) =>
        e.TryGetProperty("Status", out var s) && s.ValueKind == JsonValueKind.Object ? Str(s, "HealthRollup") ?? Str(s, "Health") : null;

    private static string? State(JsonElement e) =>
        e.TryGetProperty("Status", out var s) && s.ValueKind == JsonValueKind.Object ? Str(s, "State") : null;

    private static string? Join(params string?[] parts)
    {
        var text = string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length == 0 ? null : text.Length > 1000 ? text[..1000] : text;
    }
}

/// <summary>SNMP fallback for servers without Redfish access: the overall health of HPE (CPQHLTH) and Dell (iDRAC) agents.</summary>
public static class HardwareSnmp
{
    /// <summary>CPQHLTH-MIB cpqHeMibCondition: 1 other, 2 ok, 3 degraded, 4 failed.</summary>
    public const string HpeCondition = "1.3.6.1.4.1.232.6.1.3.0";

    /// <summary>IDRAC-MIB-SMIv2 globalSystemStatus: 1 other, 2 unknown, 3 ok, 4 nonCritical, 5 critical, 6 nonRecoverable.</summary>
    public const string DellGlobalStatus = "1.3.6.1.4.1.674.10892.5.2.1.0";

    public static string? HpeHealth(long? value) => value switch
    {
        2 => "OK",
        3 => "Warning",
        4 => "Critical",
        _ => null,
    };

    public static string? DellHealth(long? value) => value switch
    {
        3 => "OK",
        4 => "Warning",
        5 or 6 => "Critical",
        _ => null,
    };
}
