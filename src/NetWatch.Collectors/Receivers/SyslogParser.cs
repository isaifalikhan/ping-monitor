using System.Globalization;
using System.Text.RegularExpressions;

namespace NetWatch.Collectors.Receivers;

/// <summary>A parsed syslog message.</summary>
/// <param name="MessageUtc">Header timestamp, only for RFC 5424 messages (RFC 3164 timestamps have no year or zone).</param>
public sealed record ParsedSyslog(int Facility, int Severity, DateTime? MessageUtc, string? Hostname, string? AppName, string? ProcId, string? MsgId, string Message);

/// <summary>
/// Tolerant syslog parser for RFC 5424, RFC 3164 (BSD) and the common vendor variants (Cisco sequence numbers
/// and "%FAC-SEV-MNEMONIC:" tags, HP/Aruba and Juniper formats). Never throws: unparseable text becomes the message.
/// </summary>
public static partial class SyslogParser
{
    private const int DefaultFacility = 1; // user-level
    private const int DefaultSeverity = 5; // notice

    [GeneratedRegex(@"^(?:\d+:\s+)?[*.]?(?<ts>[A-Z][a-z]{2}\s+\d{1,2}(?:\s+\d{4})?\s+\d{1,2}:\d{2}:\d{2}(?:\.\d+)?(?:\s+[A-Z]{2,5}(?=:))?):?\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3164Timestamp();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})\s*", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTimestamp();

    [GeneratedRegex(@"%(?<tag>[A-Z0-9_]+(?:-[A-Z0-9_]+)*-\d-[A-Z0-9_]+):", RegexOptions.CultureInvariant)]
    private static partial Regex CiscoMnemonic();

    [GeneratedRegex(@"^(?<tag>[A-Za-z0-9_./\-]{1,48})(?:\[(?<pid>[^\]]{1,32})\])?:\s?", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    public static ParsedSyslog Parse(string raw)
    {
        var text = raw.TrimStart('﻿').TrimEnd('\0', '\r', '\n', ' ');
        var facility = DefaultFacility;
        var severity = DefaultSeverity;

        // ---- PRI
        if (text.Length > 2 && text[0] == '<')
        {
            var end = text.IndexOf('>', 1);
            if (end is > 1 and <= 4 && int.TryParse(text.AsSpan(1, end - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var pri) && pri <= 191)
            {
                facility = pri / 8;
                severity = pri % 8;
                text = text[(end + 1)..];
            }
        }

        // ---- RFC 5424: "1 TIMESTAMP HOSTNAME APP-NAME PROCID MSGID STRUCTURED-DATA MSG"
        if (text.StartsWith("1 ", StringComparison.Ordinal))
        {
            var parsed = Parse5424(text[2..], facility, severity);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return Parse3164(text, facility, severity);
    }

    private static ParsedSyslog? Parse5424(string text, int facility, int severity)
    {
        var parts = text.Split(' ', 6);
        if (parts.Length < 6)
        {
            return null;
        }

        DateTime? ts = null;
        if (parts[0] != "-" && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            ts = dto.UtcDateTime;
        }

        var rest = parts[5];
        string message;
        if (rest.StartsWith('-'))
        {
            message = rest.Length > 1 ? rest[1..].TrimStart() : string.Empty;
        }
        else if (rest.StartsWith('['))
        {
            message = SkipStructuredData(rest, out var sd);
            if (sd.Length > 0 && message.Length == 0)
            {
                message = sd;
            }
        }
        else
        {
            message = rest;
        }

        return new ParsedSyslog(facility, severity, ts, Nil(parts[1]), Nil(parts[2]), Nil(parts[3]), Nil(parts[4]), Clean(message.TrimStart('﻿')));
    }

    /// <summary>Skips "[id k="v" ...][id2 ...]" (with escaped quotes/brackets) and returns the remaining message.</summary>
    private static string SkipStructuredData(string text, out string structured)
    {
        var i = 0;
        while (i < text.Length && text[i] == '[')
        {
            var inQuotes = false;
            for (i++; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ']' && !inQuotes)
                {
                    i++;
                    break;
                }
            }
        }

        structured = text[..Math.Min(i, text.Length)];
        return i < text.Length ? text[i..].TrimStart() : string.Empty;
    }

    private static ParsedSyslog Parse3164(string text, int facility, int severity)
    {
        string? hostname = null;
        string? app = null;
        string? pid = null;

        // Timestamp: "Nov 19 10:00:00", Cisco "123: *Nov 19 10:00:00.123 PKT:", or ISO 8601.
        var ts = Rfc3164Timestamp().Match(text);
        if (ts.Success)
        {
            text = text[ts.Length..];
        }
        else if (IsoTimestamp().Match(text) is { Success: true } iso)
        {
            text = text[iso.Length..];
        }

        // Cisco messages put the mnemonic right after the timestamp, without hostname/tag.
        var cisco = CiscoMnemonic().Match(text);
        if (cisco.Success && cisco.Index <= 64)
        {
            var before = text[..cisco.Index].Trim().TrimEnd(':').Trim();
            if (before.Length > 0 && !before.Contains(' ') && LooksLikeHost(before))
            {
                hostname = before;
            }

            app = cisco.Groups["tag"].Value;
            return new ParsedSyslog(facility, severity, null, hostname, app, null, null, Clean(text[(cisco.Index + cisco.Length)..].Trim()));
        }

        // "HOSTNAME TAG[pid]: message" — the first token is a host name unless it already is the tag.
        var space = text.IndexOf(' ');
        if (space > 0 && ts.Success)
        {
            var first = text[..space];
            if (!first.EndsWith(':') && !first.Contains('[') && LooksLikeHost(first))
            {
                hostname = first;
                text = text[(space + 1)..];
            }
        }

        var tag = Tag().Match(text);
        if (tag.Success)
        {
            app = tag.Groups["tag"].Value;
            pid = tag.Groups["pid"].Success ? tag.Groups["pid"].Value : null;
            text = text[tag.Length..];
        }

        return new ParsedSyslog(facility, severity, null, hostname, app, pid, null, Clean(text.Trim()));
    }

    private static bool LooksLikeHost(string s) =>
        s.Length is > 0 and <= 255 && s.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':');

    private static string? Nil(string s) => s == "-" || s.Length == 0 ? null : s.Length > 255 ? s[..255] : s;

    private static string Clean(string s)
    {
        // Control characters (other than tab) are replaced so stored text stays printable.
        var chars = s.Select(c => char.IsControl(c) && c != '\t' ? ' ' : c).ToArray();
        var result = new string(chars);
        return result.Length <= 8192 ? result : result[..8192];
    }
}
