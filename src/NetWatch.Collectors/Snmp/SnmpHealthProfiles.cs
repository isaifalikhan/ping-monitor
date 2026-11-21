using System.Text.Json;
using System.Text.Json.Serialization;
using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Polling;
using NetWatch.Core;
using NetWatch.Core.Configuration;

namespace NetWatch.Collectors.Snmp;

/// <summary>
/// A data-driven SNMP health profile: which tables hold CPU, memory and named sensors (fans, power supplies,
/// temperatures) and how their state values map to OK / warning / critical. Built-in profiles can be replaced
/// and new vendors added without a new build through <c>snmp-profiles.json</c> in the data folder.
/// </summary>
public sealed class SnmpHealthProfile
{
    /// <summary>Vendor name as detected from sysObjectID (e.g. "HPE Aruba" for ArubaOS-CX).</summary>
    public string Vendor { get; set; } = string.Empty;

    /// <summary>Optional sysObjectID prefix to restrict the profile further.</summary>
    public string? SysObjectIdPrefix { get; set; }

    /// <summary>False while the OIDs have not been checked against a real device walk (shown in the UI and docs).</summary>
    public bool Verified { get; set; }

    /// <summary>Column with CPU % per CPU/module (averaged).</summary>
    public string? CpuColumn { get; set; }

    /// <summary>Column with memory % per module (averaged).</summary>
    public string? MemoryPercentColumn { get; set; }

    public List<SensorTableProfile> Sensors { get; set; } = new();
}

/// <summary>One sensor table: columns with the sensor name, its state and optionally a numeric value.</summary>
public sealed class SensorTableProfile
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SensorKind Kind { get; set; }
    public string NameColumn { get; set; } = string.Empty;
    public string? StateColumn { get; set; }
    public string? ValueColumn { get; set; }

    /// <summary>Multiplier for the value (e.g. 0.001 for millidegrees).</summary>
    public double ValueScale { get; set; } = 1;
    public string? Unit { get; set; }

    /// <summary>State values (text or number, case-insensitive) per result; anything else is Unknown.</summary>
    public List<string> Ok { get; set; } = new();
    public List<string> Warning { get; set; } = new();
    public List<string> Critical { get; set; } = new();
    public List<string> NotPresent { get; set; } = new();
}

/// <summary>Built-in profiles plus the optional override file. Thread-safe; the file is re-read when it changes.</summary>
public sealed class SnmpHealthProfileStore
{
    public const string FileName = "snmp-profiles.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private readonly string? _file;
    private readonly ILogger<SnmpHealthProfileStore>? _logger;
    private DateTime _loadedStamp = DateTime.MinValue;
    private IReadOnlyList<SnmpHealthProfile> _profiles = BuiltIn;

    public SnmpHealthProfileStore(NetWatchPaths? paths = null, ILogger<SnmpHealthProfileStore>? logger = null)
    {
        _file = paths is null ? null : Path.Combine(paths.Root, FileName);
        _logger = logger;
    }

    public string? LastError { get; private set; }

    /// <summary>
    /// ArubaOS-CX (enterprise 47196). The ARUBAWIRED-* table OIDs below follow the published MIB layout but have
    /// not been checked against a real switch yet (Verified = false). The standard HOST-RESOURCES-MIB and
    /// ENTITY-SENSOR-MIB fallbacks still apply. Verify with the diagnostic bundle's walk of 1.3.6.1.4.1.47196.4.1.1.3.11
    /// during the pilot and correct them in snmp-profiles.json if needed.
    /// </summary>
    public static readonly IReadOnlyList<SnmpHealthProfile> BuiltIn =
    [
        new()
        {
            Vendor = "HPE Aruba",
            Verified = false,
            Sensors =
            [
                new() { Kind = SensorKind.PowerSupply, NameColumn = "1.3.6.1.4.1.47196.4.1.1.3.11.2.1.1.1.5", StateColumn = "1.3.6.1.4.1.47196.4.1.1.3.11.2.1.1.1.6",
                    Ok = ["ok"], Warning = ["warning", "fault_input"], Critical = ["fault_output", "fault", "fault_absent"], NotPresent = ["empty", "absent"] },
                new() { Kind = SensorKind.Temperature, NameColumn = "1.3.6.1.4.1.47196.4.1.1.3.11.3.1.1.1.5", StateColumn = "1.3.6.1.4.1.47196.4.1.1.3.11.3.1.1.1.6",
                    ValueColumn = "1.3.6.1.4.1.47196.4.1.1.3.11.3.1.1.1.7", ValueScale = 0.001, Unit = "C",
                    Ok = ["normal"], Warning = ["min", "max"], Critical = ["low_critical", "critical", "emergency", "fault"] },
                new() { Kind = SensorKind.Fan, NameColumn = "1.3.6.1.4.1.47196.4.1.1.3.11.5.1.1.1.5", StateColumn = "1.3.6.1.4.1.47196.4.1.1.3.11.5.1.1.1.6",
                    Ok = ["ok"], Warning = [], Critical = ["fault"], NotPresent = ["empty", "uninitialized"] },
            ],
        },
    ];

    /// <summary>The profile for a vendor / sysObjectID, or null (then the vendor code or the generic fallback applies).</summary>
    public SnmpHealthProfile? Find(string? vendor, string? sysObjectId)
    {
        ReloadIfChanged();
        if (vendor is null)
        {
            return null;
        }

        return _profiles
            .Where(p => string.Equals(p.Vendor, vendor, StringComparison.OrdinalIgnoreCase) &&
                        (p.SysObjectIdPrefix is null || (sysObjectId?.TrimStart('.').StartsWith(p.SysObjectIdPrefix.TrimStart('.'), StringComparison.Ordinal) ?? false)))
            .OrderByDescending(p => p.SysObjectIdPrefix?.Length ?? 0)
            .FirstOrDefault();
    }

    public IReadOnlyList<SnmpHealthProfile> All
    {
        get
        {
            ReloadIfChanged();
            return _profiles;
        }
    }

    /// <summary>Merges file profiles over the built-in ones (same vendor + prefix = replace).</summary>
    public static IReadOnlyList<SnmpHealthProfile> Merge(IEnumerable<SnmpHealthProfile> builtIn, IEnumerable<SnmpHealthProfile> overrides)
    {
        var list = builtIn.ToList();
        foreach (var o in overrides.Where(o => !string.IsNullOrWhiteSpace(o.Vendor)))
        {
            list.RemoveAll(b => string.Equals(b.Vendor, o.Vendor, StringComparison.OrdinalIgnoreCase) && b.SysObjectIdPrefix == o.SysObjectIdPrefix);
            list.Add(o);
        }

        return list;
    }

    public static List<SnmpHealthProfile> ParseFile(string json) => JsonSerializer.Deserialize<List<SnmpHealthProfile>>(json, Json) ?? [];

    private void ReloadIfChanged()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            var stamp = File.Exists(_file) ? File.GetLastWriteTimeUtc(_file) : DateTime.MinValue;
            if (stamp == _loadedStamp)
            {
                return;
            }

            _profiles = stamp == DateTime.MinValue ? BuiltIn : Merge(BuiltIn, ParseFile(File.ReadAllText(_file)));
            _loadedStamp = stamp;
            LastError = null;
            if (stamp != DateTime.MinValue)
            {
                _logger?.LogInformation("Loaded SNMP health profiles from {File}", _file);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LastError = $"{FileName}: {ex.Message}";
            _logger?.LogWarning("Could not read {File}: {Error}; built-in profiles are used", _file, ex.Message);
            _loadedStamp = File.Exists(_file) ? File.GetLastWriteTimeUtc(_file) : DateTime.MinValue;
            _profiles = BuiltIn;
        }
    }
}

/// <summary>Reads a profile from a device and maps the values (pure parts unit tested).</summary>
public static class ProfileHealthReader
{
    public static async Task<HealthCollector.Health> ReadAsync(SnmpClient client, SnmpHealthProfile profile, CancellationToken ct)
    {
        async Task<Dictionary<string, ISnmpData>> W(string? oid) =>
            oid is null ? new Dictionary<string, ISnmpData>() : await InterfaceCollector.SafeWalk(client, oid, ct);

        var cpu = Average(await W(profile.CpuColumn));
        var mem = Average(await W(profile.MemoryPercentColumn));
        var sensors = new List<SensorReading>();
        foreach (var table in profile.Sensors)
        {
            sensors.AddRange(Map(table, await W(table.NameColumn), await W(table.StateColumn), await W(table.ValueColumn)));
        }

        return new HealthCollector.Health(cpu, mem, sensors);
    }

    public static IEnumerable<SensorReading> Map(SensorTableProfile table, IReadOnlyDictionary<string, ISnmpData> names, IReadOnlyDictionary<string, ISnmpData> states,
        IReadOnlyDictionary<string, ISnmpData> values)
    {
        foreach (var (index, nameData) in names.Take(48))
        {
            var state = StateOf(table, SnmpValues.Text(states.GetValueOrDefault(index)));
            double? value = SnmpValues.Double(values.GetValueOrDefault(index)) is { } raw ? Math.Round(raw * table.ValueScale, 1) : null;
            if (state == SensorState.NotPresent)
            {
                continue;
            }

            yield return new SensorReading(table.Kind, $"p{index}", SnmpValues.Text(nameData) ?? $"{table.Kind} {index}", value, table.Unit, state);
        }
    }

    public static SensorState StateOf(SensorTableProfile table, string? value)
    {
        if (value is null)
        {
            return table.StateColumn is null ? SensorState.Ok : SensorState.Unknown;
        }

        bool In(List<string> list) => list.Any(s => string.Equals(s, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return In(table.Ok) ? SensorState.Ok
            : In(table.Critical) ? SensorState.Critical
            : In(table.Warning) ? SensorState.Warning
            : In(table.NotPresent) ? SensorState.NotPresent
            : SensorState.Unknown;
    }

    private static double? Average(IReadOnlyDictionary<string, ISnmpData> rows)
    {
        var v = rows.Values.Select(SnmpValues.Double).Where(x => x is >= 0 and <= 100).ToList();
        return v.Count == 0 ? null : Math.Round(v.Average()!.Value, 1);
    }
}
