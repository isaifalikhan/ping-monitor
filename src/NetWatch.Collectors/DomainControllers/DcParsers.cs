using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NetWatch.Core;

namespace NetWatch.Collectors.DomainControllers;

/// <summary>
/// Parses Security-log events (the XML of <c>EventRecord.ToXml()</c> or a forwarded event) into the fields NetWatch
/// stores. Pure and unit tested; unknown or malformed events return null.
/// </summary>
public static class DcEventParser
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 1_000_000,
    };

    public static DcSecurityReading? Parse(string xml)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = doc.Root;
        var system = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "System");
        if (root is null || system is null)
        {
            return null;
        }

        if (!int.TryParse(Child(system, "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId) ||
            !DcEventCatalog.Events.ContainsKey(eventId))
        {
            return null;
        }

        _ = long.TryParse(Child(system, "EventRecordID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var recordId);
        var created = Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value;
        var ts = DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : DateTime.UtcNow;
        var computer = Clean(Child(system, "Computer")?.Value);

        // EventData/Data[@Name] for most events; UserData/<x>/<Field> for 1102 (log cleared).
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in root.Descendants().Where(e => e.Name.LocalName == "Data" && e.Attribute("Name") is not null))
        {
            data[d.Attribute("Name")!.Value] = d.Value;
        }

        if (root.Elements().FirstOrDefault(e => e.Name.LocalName == "UserData") is { } userData)
        {
            foreach (var e in userData.Descendants().Where(e => !e.HasElements))
            {
                data.TryAdd(e.Name.LocalName, e.Value);
            }
        }

        return FromFields(eventId, recordId, ts, computer, data);
    }

    /// <summary>Maps the named insertion strings of one event to a reading.</summary>
    public static DcSecurityReading FromFields(int eventId, long recordId, DateTime timestampUtc, string? computer, IReadOnlyDictionary<string, string> data)
    {
        string? F(string name) => data.TryGetValue(name, out var v) ? Clean(v) : null;

        var subjectUser = F("SubjectUserName");
        var subjectDomain = F("SubjectDomainName");
        var targetUser = F("TargetUserName");
        var targetDomain = F("TargetDomainName");
        string? group = null, objectName = null, detail = null;
        var workstation = F("WorkstationName") ?? F("Workstation");
        var status = F("Status");
        var subStatus = F("SubStatus");
        var failed = false;

        switch (eventId)
        {
            case 4625:
                failed = true;
                break;
            case 4771:
                failed = true;
                targetDomain = null;
                break;
            case 4768:
            case 4776:
                failed = status is not null && !IsSuccessStatus(status);
                break;
            case 4740:
                // TargetDomainName holds the caller computer (where the bad passwords came from).
                workstation = targetDomain;
                targetDomain = null;
                break;
            case 4728 or 4729 or 4732 or 4733 or 4756 or 4757:
                group = targetUser;
                targetDomain = null;
                targetUser = MemberName(F("MemberName")) ?? F("MemberSid");
                break;
            case 4719:
                detail = F("AuditPolicyChanges") is { } changes ? $"changes {changes}" : null;
                break;
            case 5136:
                objectName = F("ObjectDN");
                detail = F("AttributeLDAPDisplayName") is { } attr
                    ? $"{(F("OperationType") == "%%14675" ? "removed" : "set")} {attr}" + (F("AttributeValue") is { } val ? $" = {Short(val, 200)}" : string.Empty)
                    : null;
                break;
            case 4672:
                detail = F("PrivilegeList") is { } privileges ? string.Join(", ", privileges.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries).Take(8)) : null;
                break;
        }

        int? logonType = int.TryParse(F("LogonType"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lt) ? lt : null;
        return new DcSecurityReading(recordId, timestampUtc, eventId, computer, subjectUser, subjectDomain, targetUser, targetDomain, group, objectName,
            NormaliseIp(F("IpAddress")), workstation is null ? null : workstation.TrimStart('\\'), logonType, status, subStatus, failed, detail);
    }

    /// <summary>One-line human-readable description of an event (stored as the message).</summary>
    public static string Describe(DcSecurityReading r)
    {
        var sb = new StringBuilder(DcEventCatalog.NameOf(r.EventId));
        var target = Account(r.TargetDomain, r.TargetUser);
        var subject = Account(r.SubjectDomain, r.SubjectUser);
        switch (r.EventId)
        {
            case 4728 or 4729 or 4732 or 4733 or 4756 or 4757:
                sb.Append(CultureInfo.InvariantCulture, $": {target ?? "?"} {(DcEventCatalog.GroupAdds.Contains(r.EventId) ? "added to" : "removed from")} {r.GroupName ?? "?"}");
                break;
            case 4672 or 4719 or 1102 or 5136:
                if (subject is not null)
                {
                    sb.Append(CultureInfo.InvariantCulture, $": {subject}");
                }

                break;
            default:
                if (target is not null)
                {
                    sb.Append(CultureInfo.InvariantCulture, $": {target}");
                }

                break;
        }

        var from = r.SourceIp is not null && r.Workstation is not null ? $"{r.SourceIp} ({r.Workstation})" : r.SourceIp ?? r.Workstation;
        if (from is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" from {from}");
        }

        if (r.LogonType is { } type)
        {
            sb.Append(CultureInfo.InvariantCulture, $", logon type {type}");
        }

        if (r.Failed && (DcEventCatalog.StatusText(r.SubStatus) ?? DcEventCatalog.StatusText(r.Status)) is { } reason)
        {
            sb.Append(CultureInfo.InvariantCulture, $" ({reason})");
        }
        else if (r.Failed && r.Status is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" (status {r.Status})");
        }

        if (subject is not null && r.EventId is not (4672 or 4719 or 1102 or 5136 or 4624 or 4625 or 4768 or 4771 or 4776) && !IsComputerAccount(r.SubjectUser))
        {
            sb.Append(CultureInfo.InvariantCulture, $" by {subject}");
        }

        if (r.ObjectName is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" on {r.ObjectName}");
        }

        if (r.Detail is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"; {r.Detail}");
        }

        return Short(sb.ToString(), 1000);
    }

    public static bool IsComputerAccount(string? user) => user is { Length: > 1 } && user.EndsWith('$');

    /// <summary>Built-in service identities that receive special privileges at every boot or service start.</summary>
    public static bool IsServiceIdentity(string? user) =>
        user is null || user.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) || user.Equals("LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) ||
        user.Equals("NETWORK SERVICE", StringComparison.OrdinalIgnoreCase) || user.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase) ||
        user.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase) || user.Equals("ANONYMOUS LOGON", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessStatus(string status) =>
        status.Equals("0x0", StringComparison.OrdinalIgnoreCase) || status == "0" || status.Equals("0x00000000", StringComparison.OrdinalIgnoreCase);

    private static string? Account(string? domain, string? user) =>
        user is null ? null : domain is null || user.Contains('@') || user.Contains('\\') ? user : $"{domain}\\{user}";

    /// <summary>"CN=John Doe,OU=Users,DC=corp,DC=local" → "John Doe".</summary>
    internal static string? MemberName(string? dn)
    {
        if (dn is null || !dn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            return dn;
        }

        var sb = new StringBuilder();
        for (var i = 3; i < dn.Length; i++)
        {
            if (dn[i] == '\\' && i + 1 < dn.Length)
            {
                sb.Append(dn[++i]);
            }
            else if (dn[i] == ',')
            {
                break;
            }
            else
            {
                sb.Append(dn[i]);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    internal static string? NormaliseIp(string? ip)
    {
        if (ip is null)
        {
            return null;
        }

        if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
        {
            ip = ip[7..];
        }

        return ip is "-" or "" ? null : ip;
    }

    /// <summary>Insertion strings use "-" (and sometimes "%%" placeholders) for "not applicable".</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return value is "-" or "N/A" ? null : Short(value, 500);
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static XElement? Child(XElement parent, string localName) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
}

/// <summary>
/// Parses the <c>msDS-NCReplInboundNeighbors</c> constructed attribute (one DS_REPL_NEIGHBOR XML fragment per
/// partner and naming context) and rootDSE distinguished names. Pure and unit tested.
/// </summary>
public static class DcReplicationParser
{
    public static DcReplicationReading? ParseNeighbor(string xml)
    {
        XElement element;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            element = XElement.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        string? V(string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

        var nc = V("pszNamingContext");
        var partner = ServerNameFromDsaDn(V("pszSourceDsaDN")) ?? V("pszSourceDsaAddress");
        if (string.IsNullOrWhiteSpace(nc) || string.IsNullOrWhiteSpace(partner))
        {
            return null;
        }

        _ = int.TryParse(V("dwLastSyncResult"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result);
        _ = int.TryParse(V("cNumConsecutiveSyncFailures"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var failures);
        return new DcReplicationReading(partner, nc, Time(V("ftimeLastSyncSuccess")), Time(V("ftimeLastSyncAttempt")), result, failures);
    }

    /// <summary>"CN=NTDS Settings,CN=DC02,CN=Servers,CN=HQ,CN=Sites,CN=Configuration,..." → "DC02".</summary>
    public static string? ServerNameFromDsaDn(string? dn) => Rdn(dn, 1);

    /// <summary>"CN=DC02,CN=Servers,CN=HQ,CN=Sites,CN=Configuration,..." → "HQ".</summary>
    public static string? SiteFromServerDn(string? dn)
    {
        if (dn is null)
        {
            return null;
        }

        var parts = SplitDn(dn);
        var sites = parts.FindIndex(p => p.Equals("CN=Sites", StringComparison.OrdinalIgnoreCase));
        return sites > 0 ? parts[sites - 1][3..] : null;
    }

    /// <summary>"DC=corp,DC=example,DC=local" → "corp.example.local".</summary>
    public static string? DnsFromDn(string? dn)
    {
        if (dn is null)
        {
            return null;
        }

        var labels = SplitDn(dn).Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p[3..]).ToList();
        return labels.Count == 0 ? null : string.Join('.', labels);
    }

    /// <summary>Short label of a naming context: "DC=corp,DC=local" → "corp.local", "CN=Configuration,DC=..." → "Configuration".</summary>
    public static string NamingContextLabel(string nc)
    {
        var first = SplitDn(nc).FirstOrDefault() ?? nc;
        if (first.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
        {
            return first.StartsWith("DC=DomainDnsZones", StringComparison.OrdinalIgnoreCase) ? "DomainDnsZones"
                : first.StartsWith("DC=ForestDnsZones", StringComparison.OrdinalIgnoreCase) ? "ForestDnsZones"
                : DnsFromDn(nc) ?? nc;
        }

        return first.Length > 3 ? first[3..] : nc;
    }

    private static string? Rdn(string? dn, int index)
    {
        if (dn is null)
        {
            return null;
        }

        var parts = SplitDn(dn);
        return parts.Count > index && parts[index].Length > 3 ? parts[index][3..] : null;
    }

    private static List<string> SplitDn(string dn)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\' && i + 1 < dn.Length)
            {
                sb.Append(dn[++i]);
            }
            else if (dn[i] == ',')
            {
                parts.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(dn[i]);
            }
        }

        if (sb.Length > 0)
        {
            parts.Add(sb.ToString().Trim());
        }

        return parts;
    }

    private static DateTime? Time(string? text)
    {
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
        {
            return null;
        }

        // "Never" is reported as 1601-01-01.
        return t.Year < 1980 ? null : DateTime.SpecifyKind(t, DateTimeKind.Utc);
    }
}
