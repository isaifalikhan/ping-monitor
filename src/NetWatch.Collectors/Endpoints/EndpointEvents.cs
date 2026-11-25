using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using NetWatch.Collectors.Inventory;
using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Events;

namespace NetWatch.Collectors.Endpoints;

/// <summary>One event forwarded (WEF) from a workstation or member server.</summary>
public sealed record EndpointEventReading(
    long RecordId,
    DateTime TimestampUtc,
    string Channel,
    string? Provider,
    int EventId,
    int Level,
    string? Computer,
    IReadOnlyDictionary<string, string> Data,
    string? RenderedMessage);

/// <summary>What HIT SIEM makes of an endpoint event: category, severity, message and alert fields.</summary>
public sealed record EndpointEventInfo(string Category, Severity Severity, string Message, IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// The endpoint events the WEF subscription of the deployment kit forwards, and how each becomes a stored
/// Windows event plus an alert signal (EventKeys.EndpointEvent).
/// </summary>
public static partial class EndpointEventCatalog
{
    public const string Defender = "Microsoft-Windows-Windows Defender/Operational";
    public const string Firewall = "Microsoft-Windows-Windows Firewall With Advanced Security/Firewall";

    /// <summary>Channel → event IDs. Also used to build the subscription query of the deployment kit.</summary>
    public static readonly IReadOnlyDictionary<string, int[]> Events = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Security"] = [4688, 4625, 4720, 4726, 4732, 4733, 4697, 1102, 6416],
        ["System"] = [7045, 104],
        ["Application"] = [11707, 11724],
        [Defender] = [1116, 1117, 5001, 5010, 5012],
        [Firewall] = [2003, 2004, 2005, 2006, 2033, 2052, 2097, 2099],
    };

    private const string AdministratorsSid = "S-1-5-32-544";

    public static bool Wants(string? channel, int eventId) =>
        channel is not null && Events.TryGetValue(channel, out var ids) && ids.Contains(eventId);

    /// <summary>Log name stored with the event (the WindowsEvents column holds 64 characters).</summary>
    public static string ShortLog(string channel) => channel switch
    {
        Defender => "Defender",
        Firewall => "Firewall",
        _ => channel.Length <= 64 ? channel : channel[..64],
    };

    private static readonly XmlReaderSettings ReaderSettings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    /// <summary>Parses the XML of a forwarded event; null when it is not one of the catalog events.</summary>
    public static EndpointEventReading? Parse(string xml)
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

        var channel = Child(system, "Channel")?.Value;
        if (!int.TryParse(Child(system, "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId) || !Wants(channel, eventId))
        {
            return null;
        }

        _ = long.TryParse(Child(system, "EventRecordID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var recordId);
        _ = int.TryParse(Child(system, "Level")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level);
        var created = Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value;
        var ts = DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : DateTime.UtcNow;

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unnamed = 0;
        foreach (var d in root.Descendants().Where(e => e.Name.LocalName == "Data"))
        {
            if (d.Attribute("Name")?.Value is { Length: > 0 } name)
            {
                data[name] = d.Value;
            }
            else if (d.Value.Length > 0)
            {
                data["data" + unnamed++.ToString(CultureInfo.InvariantCulture)] = d.Value;
            }
        }

        // 1102 / 104 (log cleared) carry their values in UserData.
        if (root.Elements().FirstOrDefault(e => e.Name.LocalName == "UserData") is { } userData)
        {
            foreach (var e in userData.Descendants().Where(e => !e.HasElements))
            {
                data.TryAdd(e.Name.LocalName, e.Value);
            }
        }

        var rendered = root.Elements().FirstOrDefault(e => e.Name.LocalName == "RenderingInfo")?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
        return new EndpointEventReading(recordId, ts, channel!, Child(system, "Provider")?.Attribute("Name")?.Value, eventId, level == 0 ? 4 : level,
            Clean(Child(system, "Computer")?.Value), data, string.IsNullOrWhiteSpace(rendered) ? null : rendered.Trim());
    }

    /// <summary>Category, severity, one-line message and alert fields of an event.</summary>
    public static EndpointEventInfo Describe(EndpointEventReading e)
    {
        var d = e.Data;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["endpoint"] = e.Computer ?? string.Empty };
        string V(string key) => d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) && v != "-" ? v.Trim() : string.Empty;
        void F(string field, string value)
        {
            if (value.Length > 0)
            {
                fields[field] = value;
            }
        }

        string category;
        var severity = Severity.Info;
        string message;
        switch (e.Channel, e.EventId)
        {
            case ("Security", 4688):
            {
                category = "Process";
                var image = V("NewProcessName");
                var cmd = V("CommandLine");
                var user = Account(V("SubjectDomainName"), V("SubjectUserName"));
                var parent = V("ParentProcessName");
                F("process", FileName(image));
                F("image", image);
                F("commandLine", cmd);
                F("user", user);
                F("parent", FileName(parent));
                var suspicious = SuspiciousCommand().IsMatch(cmd.Length > 0 ? cmd : image);
                fields["suspicious"] = suspicious ? "true" : "false";
                severity = suspicious ? Severity.Major : Severity.Info;
                message = $"Process started: {(cmd.Length > 0 ? cmd : image)} · user {user}{(parent.Length > 0 ? $" · parent {FileName(parent)}" : string.Empty)}";
                break;
            }

            case ("Security", 4625):
            {
                category = "LogonFailure";
                var target = Account(V("TargetDomainName"), V("TargetUserName"));
                F("targetUser", target);
                F("sourceIp", V("IpAddress"));
                F("logonType", V("LogonType"));
                F("source", V("IpAddress").Length > 0 ? V("IpAddress") : V("WorkstationName"));
                fields["failed"] = "true";
                message = $"Failed logon for {target} (logon type {V("LogonType")}){(V("IpAddress").Length > 0 ? " from " + V("IpAddress") : string.Empty)}";
                break;
            }

            case ("Security", 4720 or 4726):
            {
                category = "LocalAccount";
                var target = V("TargetUserName");
                F("targetUser", target);
                F("user", Account(V("SubjectDomainName"), V("SubjectUserName")));
                severity = Severity.Warning;
                message = e.EventId == 4720 ? $"Local user account created: {target} by {fields.GetValueOrDefault("user")}" : $"Local user account deleted: {target} by {fields.GetValueOrDefault("user")}";
                break;
            }

            case ("Security", 4732 or 4733):
            {
                category = "LocalGroup";
                var group = V("TargetUserName");
                var member = V("MemberName").Length > 0 ? V("MemberName") : V("MemberSid");
                var privileged = string.Equals(V("TargetSid"), AdministratorsSid, StringComparison.OrdinalIgnoreCase);
                F("group", group);
                F("member", member);
                F("user", Account(V("SubjectDomainName"), V("SubjectUserName")));
                fields["privileged"] = privileged ? "true" : "false";
                fields["added"] = e.EventId == 4732 ? "true" : "false";
                severity = privileged && e.EventId == 4732 ? Severity.Major : Severity.Info;
                message = e.EventId == 4732 ? $"Member {member} added to local group {group} by {fields.GetValueOrDefault("user")}" : $"Member {member} removed from local group {group}";
                break;
            }

            case ("Security", 4697):
            case ("System", 7045):
            {
                category = "Service";
                var name = e.EventId == 7045 ? V("ServiceName") : V("ServiceName");
                var image = e.EventId == 7045 ? V("ImagePath") : V("ServiceFileName");
                F("service", name);
                F("image", image);
                F("user", e.EventId == 7045 ? V("AccountName") : V("ServiceAccount"));
                fields["suspicious"] = SuspiciousCommand().IsMatch(image) ? "true" : "false";
                severity = Severity.Warning;
                message = $"Service installed: {name} ({image})";
                break;
            }

            case ("Security", 1102):
            case ("System", 104):
            {
                category = "LogCleared";
                F("user", Account(V("SubjectDomainName"), V("SubjectUserName")));
                severity = Severity.Critical;
                message = e.EventId == 1102 ? $"Security log cleared by {fields.GetValueOrDefault("user", "?")}" : $"{(V("Channel").Length > 0 ? V("Channel") : "An event")} log cleared";
                break;
            }

            case ("Security", 6416):
            {
                category = "Device";
                var name = V("DeviceDescription");
                var id = V("DeviceId");
                var storage = id.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase) || string.Equals(V("ClassName"), "DiskDrive", StringComparison.OrdinalIgnoreCase);
                F("deviceName", name);
                F("deviceId", id);
                F("deviceClass", V("ClassName"));
                fields["usbStorage"] = storage ? "true" : "false";
                severity = storage ? Severity.Warning : Severity.Info;
                message = $"New external device recognised: {name} ({V("ClassName")}) {id}";
                break;
            }

            case ("Application", 11707 or 11724):
            {
                category = "Software";
                var text = V("data0");
                var product = Product().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : text;
                F("product", product);
                fields["installed"] = e.EventId == 11707 ? "true" : "false";
                message = e.EventId == 11707 ? $"Software installed: {product}" : $"Software removed: {product}";
                break;
            }

            case (Defender, 1116 or 1117):
            {
                category = "Malware";
                var threat = V("Threat Name");
                F("threat", threat);
                F("path", V("Path"));
                F("user", V("Detection User"));
                F("process", V("Process Name"));
                F("action", V("Action Name"));
                severity = Severity.Critical;
                message = e.EventId == 1116
                    ? $"Malware detected: {threat} in {V("Path")} (user {V("Detection User")})"
                    : $"Malware action {V("Action Name")}: {threat} in {V("Path")}";
                break;
            }

            case (Defender, 5001 or 5010 or 5012):
            {
                category = "Antivirus";
                severity = Severity.Major;
                message = e.EventId switch
                {
                    5001 => "Microsoft Defender real-time protection was turned off",
                    5010 => "Microsoft Defender scanning for malware and other potentially unwanted software is disabled",
                    _ => "Microsoft Defender scanning for viruses is disabled",
                };
                break;
            }

            case (Firewall, _):
            {
                category = "Firewall";
                var rule = V("RuleName");
                F("rule", rule);
                F("user", V("ModifyingUser"));
                F("process", FileName(V("ModifyingApplication")));
                severity = e.EventId is 2003 or 2033 ? Severity.Warning : Severity.Info;
                message = e.EventId switch
                {
                    2003 => $"Windows Firewall setting changed ({V("Profiles")} profile): {V("SettingType")} = {V("SettingValueString")}",
                    2004 or 2097 => $"Firewall rule added: {rule} ({V("ApplicationPath")})",
                    2005 or 2099 => $"Firewall rule changed: {rule}",
                    2006 or 2052 => $"Firewall rule deleted: {rule}",
                    2033 => "All Windows Firewall rules were deleted",
                    _ => $"Windows Firewall event {e.EventId}",
                };
                break;
            }

            default:
                category = "Other";
                message = e.RenderedMessage ?? $"{e.Channel} event {e.EventId}";
                break;
        }

        fields["category"] = category;
        return new EndpointEventInfo(category, severity, message, fields);
    }

    /// <summary>
    /// Stores endpoint events of one device (skipping ones already stored) and publishes their alert signals.
    /// Process starts (4688) are stored only when enabled in settings, but always evaluated.
    /// </summary>
    public static async Task<int> IngestAsync(DeviceSnapshot device, IReadOnlyList<EndpointEventReading> readings, bool storeProcessStarts, EventsDbContext events, IAlertSignalSink alerts, CancellationToken ct)
    {
        var stored = 0;
        foreach (var group in readings.GroupBy(r => ShortLog(r.Channel)))
        {
            var log = group.Key;
            var ids = group.Select(r => r.RecordId).ToList();
            var min = ids.Min();
            var max = ids.Max();
            var existing = (await events.WindowsEvents.AsNoTracking()
                    .Where(w => w.DeviceId == device.Id && w.LogName == log && w.RecordId >= min && w.RecordId <= max)
                    .Select(w => w.RecordId).ToListAsync(ct))
                .ToHashSet();
            foreach (var r in group.OrderBy(r => r.RecordId))
            {
                if (!existing.Add(r.RecordId))
                {
                    continue;
                }

                var info = Describe(r);
                var keep = storeProcessStarts || r.EventId != 4688 || info.Fields.GetValueOrDefault("suspicious") == "true";
                if (keep)
                {
                    events.WindowsEvents.Add(new WindowsEventEntry
                    {
                        DeviceId = device.Id,
                        LogName = log,
                        RecordId = r.RecordId,
                        TimestampUtc = r.TimestampUtc,
                        Level = r.Level,
                        EventId = r.EventId,
                        Provider = r.Provider is { Length: > 200 } p ? p[..200] : r.Provider,
                        Message = info.Message.Length > 8192 ? info.Message[..8192] : info.Message,
                    });
                    stored++;
                }

                alerts.Publish(new EventSignal(device.Id, EventKeys.EndpointEvent, info.Message, r.TimestampUtc, info.Severity, r.Provider, r.EventId, null, info.Fields));
            }
        }

        await events.SaveChangesAsync(ct);
        return stored;
    }

    private static string Account(string domain, string user) => user.Length == 0 ? string.Empty : domain.Length == 0 ? user : $"{domain}\\{user}";

    private static string FileName(string path) => path.Length == 0 ? string.Empty : Path.GetFileName(path.Replace('/', '\\').Split('\\')[^1]);

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Command lines typical for attacks: encoded PowerShell, credential dumping, shadow-copy deletion, log clearing, LOLBins downloading.</summary>
    [GeneratedRegex(@"\s-e(nc|ncodedcommand|c)?\s+[A-Za-z0-9+/=]{20,}|mimikatz|sekurlsa|procdump(64)?(\.exe)?\s.*lsass|comsvcs(\.dll)?.*minidump|vssadmin(\.exe)?\s+delete\s+shadows|wmic(\.exe)?\s+shadowcopy\s+delete|wbadmin(\.exe)?\s+delete|bcdedit(\.exe)?\s.*recoveryenabled\s+no|wevtutil(\.exe)?\s+cl\s|certutil(\.exe)?\s.*-urlcache|bitsadmin(\.exe)?\s.*/transfer|reg(\.exe)?\s+save\s+hklm\\(sam|security|system)|ntdsutil|psexesvc|invoke-(mimikatz|expression).*downloadstring|net(1)?(\.exe)?\s+user\s+\S+\s+\S+\s+/add",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex SuspiciousCommand();

    [GeneratedRegex(@"Product:\s*(.+?)\s*--", RegexOptions.CultureInvariant)]
    private static partial Regex Product();
}
