using System.Globalization;
using System.Net;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using NetWatch.Core;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;

namespace NetWatch.Web.Csv;

/// <summary>One parsed CSV row (or a parse error for that row).</summary>
public sealed record DeviceCsvRow(
    int Line,
    string Name,
    string IpAddress,
    DeviceType Type,
    string? Location,
    string? GroupPath,
    IReadOnlyList<string> Tags,
    string? SnmpCredential,
    int? PollIntervalSec,
    string? Parent,
    string? Notes);

public sealed record CsvError(int Line, string Message);

public sealed record DeviceCsvParseResult(IReadOnlyList<DeviceCsvRow> Rows, IReadOnlyList<CsvError> Errors);

public sealed record DeviceImportResult(int Created, int Skipped, IReadOnlyList<CsvError> Errors);

/// <summary>
/// Parses the device import CSV. Header names are case-insensitive. Required: name, ip.
/// Optional: type, location, group (path "Site/Building/Floor"), tags (separated by ';'), snmp_credential
/// (credential profile name), poll_interval (seconds), parent (name or IP of upstream device), notes.
/// </summary>
public static class DeviceCsvParser
{
    public const string Template =
        "name,ip,type,location,group,tags,snmp_credential,poll_interval,parent,notes\r\n" +
        "CORE-SW-01,10.0.0.1,Switch,Server room,HQ/Data Center/Rack 01,core;cisco,SNMPv3 network,60,,Core switch\r\n" +
        "ACC-SW-101,10.0.1.11,Switch,Floor 1 IDF,HQ/Building A/Floor 1,access,SNMPv3 network,60,CORE-SW-01,\r\n" +
        "APP-SRV-01,10.0.10.21,Server,Rack 02,HQ/Data Center/Rack 02,erp,,60,CORE-SW-01,ERP application server\r\n";

    public static DeviceCsvParseResult Parse(Stream stream)
    {
        var rows = new List<DeviceCsvRow>();
        var errors = new List<CsvError>();
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = a => a.Header.Trim().ToLowerInvariant().Replace(" ", "_"),
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim,
            DetectDelimiter = true,
        };

        using var csv = new CsvReader(reader, config);
        if (!csv.Read() || !csv.ReadHeader())
        {
            errors.Add(new CsvError(1, "The file is empty or has no header row."));
            return new DeviceCsvParseResult(rows, errors);
        }

        var headers = csv.HeaderRecord?.Select(h => h.Trim().ToLowerInvariant().Replace(" ", "_")).ToHashSet() ?? new HashSet<string>();
        if (!headers.Contains("name") || !(headers.Contains("ip") || headers.Contains("ip_address")))
        {
            errors.Add(new CsvError(1, "Header must contain at least the columns 'name' and 'ip'."));
            return new DeviceCsvParseResult(rows, errors);
        }

        while (csv.Read())
        {
            var line = csv.Parser.Row;
            string? Field(string name) => csv.TryGetField<string>(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

            var name = Field("name");
            var ipText = Field("ip") ?? Field("ip_address");
            if (name is null && ipText is null)
            {
                continue;
            }

            if (name is null || name.Length > 200)
            {
                errors.Add(new CsvError(line, "Name is missing or longer than 200 characters."));
                continue;
            }

            if (!IPAddress.TryParse(ipText, out var ip))
            {
                errors.Add(new CsvError(line, $"'{ipText}' is not a valid IP address."));
                continue;
            }

            var type = DeviceType.Switch;
            if (Field("type") is { } typeText && !TryParseType(typeText, out type))
            {
                errors.Add(new CsvError(line, $"Unknown type '{typeText}'. Use: {string.Join(", ", Enum.GetNames<DeviceType>())}."));
                continue;
            }

            int? interval = null;
            if (Field("poll_interval") is { } intervalText)
            {
                if (!int.TryParse(intervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds is < 15 or > 3600)
                {
                    errors.Add(new CsvError(line, "poll_interval must be a number of seconds between 15 and 3600."));
                    continue;
                }

                interval = seconds;
            }

            var tags = (Field("tags") ?? string.Empty)
                .Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length <= 64)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            rows.Add(new DeviceCsvRow(line, name, ip.ToString(), type, Field("location"), Field("group"), tags,
                Field("snmp_credential"), interval, Field("parent"), Field("notes")));
        }

        return new DeviceCsvParseResult(rows, errors);
    }

    internal static bool TryParseType(string text, out DeviceType type)
    {
        var normalised = text.Trim().Replace(" ", string.Empty);
        if (normalised.Equals("DC", StringComparison.OrdinalIgnoreCase))
        {
            type = DeviceType.DomainController;
            return true;
        }

        if (normalised.Equals("ESXi", StringComparison.OrdinalIgnoreCase) || normalised.Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            type = DeviceType.Hypervisor;
            return true;
        }

        return Enum.TryParse(normalised, ignoreCase: true, out type) && Enum.IsDefined(type);
    }
}

/// <summary>Creates devices (and missing groups) from parsed CSV rows. Existing IPs are skipped, never overwritten.</summary>
public static class DeviceCsvImporter
{
    public static async Task<DeviceImportResult> ImportAsync(DeviceCsvParseResult parsed, NetWatchDbContext db, int defaultInterval, CancellationToken ct)
    {
        var errors = parsed.Errors.ToList();
        var skipped = 0;
        var existingIps = (await db.Devices.Select(d => d.IpAddress).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var credentials = await db.CredentialProfiles
            .Where(c => c.Kind == CredentialKind.SnmpV2c || c.Kind == CredentialKind.SnmpV3)
            .ToDictionaryAsync(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase, ct);
        var groups = await db.Groups.ToListAsync(ct);
        var now = DateTime.UtcNow;
        var created = new List<(DeviceCsvRow Row, Device Device)>();

        foreach (var row in parsed.Rows)
        {
            if (!existingIps.Add(row.IpAddress))
            {
                skipped++;
                errors.Add(new CsvError(row.Line, $"{row.IpAddress} already exists; row skipped."));
                continue;
            }

            int? credentialId = null;
            if (row.SnmpCredential is not null)
            {
                if (!credentials.TryGetValue(row.SnmpCredential, out var id))
                {
                    errors.Add(new CsvError(row.Line, $"SNMP credential profile '{row.SnmpCredential}' not found; device created without SNMP."));
                }
                else
                {
                    credentialId = id;
                }
            }

            var device = new Device
            {
                Name = row.Name,
                IpAddress = row.IpAddress,
                Type = row.Type,
                Os = row.Type is DeviceType.Switch or DeviceType.Router or DeviceType.Firewall ? OsKind.NetworkOs : OsKind.Unknown,
                Location = row.Location,
                Group = row.GroupPath is null ? null : EnsureGroupPath(row.GroupPath, groups, db),
                PollIntervalSec = row.PollIntervalSec ?? defaultInterval,
                IsEnabled = true,
                Notes = row.Notes,
                Status = DeviceStatus.Unknown,
                StatusSinceUtc = now,
                CreatedUtc = now,
                UpdatedUtc = now,
                Tags = row.Tags.Select(t => new DeviceTag { Tag = t }).ToList(),
                Credentials = credentialId is { } cid ? [new DeviceCredential { Purpose = CredentialPurpose.Snmp, CredentialProfileId = cid }] : [],
            };
            db.Devices.Add(device);
            created.Add((row, device));
        }

        await db.SaveChangesAsync(ct);

        // Resolve parents in a second pass, so a parent can be defined later in the same file.
        var withParent = created.Where(c => c.Row.Parent is not null).ToList();
        if (withParent.Count > 0)
        {
            var lookup = await db.Devices.Select(d => new { d.Id, d.Name, d.IpAddress }).ToListAsync(ct);
            foreach (var (row, device) in withParent)
            {
                var parent = lookup.FirstOrDefault(d => string.Equals(d.Name, row.Parent, StringComparison.OrdinalIgnoreCase) || d.IpAddress == row.Parent);
                if (parent is null || parent.Id == device.Id)
                {
                    errors.Add(new CsvError(row.Line, $"Parent '{row.Parent}' not found; dependency not set."));
                    continue;
                }

                device.ParentDeviceId = parent.Id;
            }

            await db.SaveChangesAsync(ct);
        }

        return new DeviceImportResult(created.Count, skipped, errors.OrderBy(e => e.Line).ToList());
    }

    private static DeviceGroup EnsureGroupPath(string path, List<DeviceGroup> groups, NetWatchDbContext db)
    {
        var parts = path.Split(['/', '>', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DeviceGroup? parent = null;
        for (var i = 0; i < parts.Length; i++)
        {
            var name = parts[i].Length > 128 ? parts[i][..128] : parts[i];
            var existing = groups.FirstOrDefault(g =>
                string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase) &&
                (parent is null ? g.ParentId is null && g.Parent is null : (g.Parent == parent || (parent.Id != 0 && g.ParentId == parent.Id))));
            if (existing is null)
            {
                existing = new DeviceGroup
                {
                    Name = name,
                    Parent = parent,
                    Kind = i switch { 0 => GroupKind.Site, 1 => GroupKind.Building, 2 => GroupKind.Floor, _ => GroupKind.Rack },
                };
                groups.Add(existing);
                db.Groups.Add(existing);
            }

            parent = existing;
        }

        return parent!;
    }
}
