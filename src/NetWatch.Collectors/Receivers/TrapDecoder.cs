using System.Text.Json;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using NetWatch.Collectors.Snmp;
using NetWatch.Core;

namespace NetWatch.Collectors.Receivers;

public sealed record TrapVarbind(string Oid, string? Name, string Value);

/// <summary>A decoded trap/inform, independent of the SNMP version.</summary>
/// <param name="AgentAddress">SNMPv1 agent-addr field (the originating device when a trap is relayed).</param>
public sealed record DecodedTrap(string Version, string TrapOid, string? Name, Severity Severity, IReadOnlyList<TrapVarbind> Varbinds, System.Net.IPAddress? AgentAddress)
{
    /// <summary>ifIndex carried by linkUp/linkDown (IF-MIB ifIndex varbind), if present.</summary>
    public int? IfIndex =>
        Varbinds.FirstOrDefault(v => v.Oid.StartsWith(TrapCatalog.IfIndexPrefix, StringComparison.Ordinal)) is { } v && int.TryParse(v.Value, out var i) ? i : null;

    public bool IsLinkEvent => TrapOid is TrapCatalog.LinkDown or TrapCatalog.LinkUp;

    public bool IsRestart => TrapOid is TrapCatalog.ColdStart or TrapCatalog.WarmStart;

    public string VarbindsJson() => JsonSerializer.Serialize(Varbinds.Select(v => new { oid = v.Oid, name = v.Name, value = v.Value }));

    /// <summary>One-line summary, e.g. "linkDown: Gi1/0/5 (ifIndex 5)".</summary>
    public string Summary(string? interfaceName = null)
    {
        var label = Name ?? TrapOid;
        if (IsLinkEvent && IfIndex is { } idx)
        {
            var ifName = interfaceName ?? Varbinds.FirstOrDefault(v => v.Name is "ifName" or "ifDescr")?.Value;
            return ifName is null ? $"{label}: ifIndex {idx}" : $"{label}: {ifName} (ifIndex {idx})";
        }

        var details = string.Join("; ", Varbinds.Take(6).Select(v => $"{v.Name ?? v.Oid}={v.Value}"));
        var text = details.Length > 0 ? $"{label}: {details}" : label;
        return text.Length <= 1000 ? text : text[..1000];
    }
}

/// <summary>Decodes SNMP v1/v2c/v3 traps and informs.</summary>
public static class TrapDecoder
{
    private const string SysUpTime = "1.3.6.1.2.1.1.3.0";
    private const string SnmpTrapOid = "1.3.6.1.6.3.1.1.4.1.0";

    public static DecodedTrap? Decode(ISnmpMessage message)
    {
        switch (message)
        {
            case TrapV1Message v1:
            {
                var generic = (int)v1.Generic;
                var oid = generic < 6
                    ? $"1.3.6.1.6.3.1.1.5.{generic + 1}"
                    : $"{v1.Enterprise}.0.{v1.Specific}";
                return Build("v1", oid, v1.Pdu().Variables, v1.AgentAddress);
            }

            case TrapV2Message:
            case InformRequestMessage:
            {
                // SharpSnmpLib moves sysUpTime/snmpTrapOID out of the varbind list into Enterprise/TimeStamp;
                // fall back to the varbind for agents/versions where it stays in the list.
                var variables = message.Pdu().Variables;
                var oid = message switch
                {
                    TrapV2Message t => t.Enterprise?.ToString(),
                    InformRequestMessage i => i.Enterprise?.ToString(),
                    _ => null,
                };
                oid ??= variables.FirstOrDefault(v => v.Id.ToString() == SnmpTrapOid)?.Data.ToString();
                if (string.IsNullOrEmpty(oid))
                {
                    return null;
                }

                var rest = variables.Where(v => v.Id.ToString() is not (SysUpTime or SnmpTrapOid)).ToList();
                return Build(message.Version == VersionCode.V3 ? "v3" : "v2c", oid, rest, null);
            }

            default:
                return null;
        }
    }

    private static DecodedTrap Build(string version, string oid, IList<Variable> variables, System.Net.IPAddress? agent)
    {
        oid = oid.TrimStart('.');
        var (name, severity) = TrapCatalog.Lookup(oid);
        var varbinds = variables
            .Take(50)
            .Select(v =>
            {
                var id = v.Id.ToString();
                var value = v.Data is ObjectIdentifier o ? o.ToString() : SnmpValues.Text(v.Data) ?? string.Empty;
                return new TrapVarbind(id, TrapCatalog.VarbindName(id), value.Length <= 500 ? value : value[..500]);
            })
            .ToList();
        return new DecodedTrap(version, oid, name, severity, varbinds, agent is null || agent.Equals(System.Net.IPAddress.Any) ? null : agent);
    }
}

/// <summary>Names and severities of well-known traps (standard MIBs plus Cisco, HP and Juniper chassis traps).</summary>
public static class TrapCatalog
{
    public const string ColdStart = "1.3.6.1.6.3.1.1.5.1";
    public const string WarmStart = "1.3.6.1.6.3.1.1.5.2";
    public const string LinkDown = "1.3.6.1.6.3.1.1.5.3";
    public const string LinkUp = "1.3.6.1.6.3.1.1.5.4";
    public const string AuthenticationFailure = "1.3.6.1.6.3.1.1.5.5";
    public const string IfIndexPrefix = "1.3.6.1.2.1.2.2.1.1.";

    private static readonly Dictionary<string, (string Name, Severity Severity)> Traps = new(StringComparer.Ordinal)
    {
        [ColdStart] = ("coldStart", Severity.Warning),
        [WarmStart] = ("warmStart", Severity.Info),
        [LinkDown] = ("linkDown", Severity.Warning),
        [LinkUp] = ("linkUp", Severity.Info),
        [AuthenticationFailure] = ("authenticationFailure", Severity.Warning),
        ["1.3.6.1.6.3.1.1.5.6"] = ("egpNeighborLoss", Severity.Warning),
        ["1.3.6.1.2.1.15.7.1"] = ("bgpEstablished", Severity.Info),
        ["1.3.6.1.2.1.15.7.2"] = ("bgpBackwardTransition", Severity.Warning),
        ["1.3.6.1.2.1.47.2.0.1"] = ("entConfigChange", Severity.Info),
        ["1.3.6.1.2.1.17.0.1"] = ("newRoot (STP)", Severity.Warning),
        ["1.3.6.1.2.1.17.0.2"] = ("topologyChange (STP)", Severity.Info),

        // Cisco
        ["1.3.6.1.4.1.9.9.43.2.0.1"] = ("ciscoConfigManEvent", Severity.Info),
        ["1.3.6.1.4.1.9.9.41.2.0.1"] = ("clogMessageGenerated", Severity.Info),
        ["1.3.6.1.4.1.9.9.13.3.0.1"] = ("ciscoEnvMonShutdownNotification", Severity.Critical),
        ["1.3.6.1.4.1.9.9.13.3.0.2"] = ("ciscoEnvMonVoltageNotification", Severity.Major),
        ["1.3.6.1.4.1.9.9.13.3.0.3"] = ("ciscoEnvMonTemperatureNotification", Severity.Major),
        ["1.3.6.1.4.1.9.9.13.3.0.4"] = ("ciscoEnvMonFanNotification", Severity.Major),
        ["1.3.6.1.4.1.9.9.13.3.0.5"] = ("ciscoEnvMonRedundantSupplyNotification", Severity.Major),

        // Juniper chassis (JUNIPER-MIB jnxChassisTraps)
        ["1.3.6.1.4.1.2636.4.1.1"] = ("jnxPowerSupplyFailure", Severity.Major),
        ["1.3.6.1.4.1.2636.4.1.2"] = ("jnxFanFailure", Severity.Major),
        ["1.3.6.1.4.1.2636.4.1.3"] = ("jnxOverTemperature", Severity.Major),
        ["1.3.6.1.4.1.2636.4.2.1"] = ("jnxPowerSupplyOK", Severity.Info),
        ["1.3.6.1.4.1.2636.4.2.2"] = ("jnxFanOK", Severity.Info),
        ["1.3.6.1.4.1.2636.4.2.3"] = ("jnxTemperatureOK", Severity.Info),
    };

    private static readonly Dictionary<string, string> VarbindColumns = new(StringComparer.Ordinal)
    {
        ["1.3.6.1.2.1.2.2.1.1"] = "ifIndex",
        ["1.3.6.1.2.1.2.2.1.2"] = "ifDescr",
        ["1.3.6.1.2.1.2.2.1.3"] = "ifType",
        ["1.3.6.1.2.1.2.2.1.7"] = "ifAdminStatus",
        ["1.3.6.1.2.1.2.2.1.8"] = "ifOperStatus",
        ["1.3.6.1.2.1.31.1.1.1.1"] = "ifName",
        ["1.3.6.1.2.1.31.1.1.1.18"] = "ifAlias",
        ["1.3.6.1.2.1.1.5"] = "sysName",
        ["1.3.6.1.4.1.9.9.41.1.2.3.1.2"] = "clogHistFacility",
        ["1.3.6.1.4.1.9.9.41.1.2.3.1.4"] = "clogHistMsgName",
        ["1.3.6.1.4.1.9.9.41.1.2.3.1.5"] = "clogHistMsgText",
    };

    public static (string? Name, Severity Severity) Lookup(string oid) =>
        Traps.TryGetValue(oid, out var t) ? (t.Name, t.Severity) : (null, Severity.Info);

    /// <summary>Friendly name of a varbind (column name without the index), or null.</summary>
    public static string? VarbindName(string oid)
    {
        var dot = oid.LastIndexOf('.');
        while (dot > 0)
        {
            if (VarbindColumns.TryGetValue(oid[..dot], out var name))
            {
                return name;
            }

            dot = oid.LastIndexOf('.', dot - 1);
        }

        return null;
    }
}
