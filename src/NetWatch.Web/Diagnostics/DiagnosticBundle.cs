using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lextm.SharpSnmpLib;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Scheduling;
using NetWatch.Collectors.Snmp;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Credentials;
using NetWatch.Data.Config;

namespace NetWatch.Web.Diagnostics;

/// <summary>
/// One zip the administrator carries out of the air gap on USB: logs of the last 24 h, connection-test results,
/// SNMP walks of selected devices (system, ENTITY-MIB, ENTITY-SENSOR, HOST-RESOURCES processors, vendor health
/// subtrees, LLDP), poll statistics, the inventory without secrets and the configuration with every secret
/// removed. No credential value, community, password or key is ever written.
/// </summary>
public sealed partial class DiagnosticBundleService
{
    /// <summary>Subtrees walked per device (bounded number of rows each).</summary>
    public static readonly IReadOnlyList<(string Name, string Oid)> WalkTrees =
    [
        ("system", "1.3.6.1.2.1.1"),
        ("entPhysicalTable", "1.3.6.1.2.1.47.1.1.1"),
        ("entPhySensorTable", "1.3.6.1.2.1.99.1.1"),
        ("hrProcessorLoad", "1.3.6.1.2.1.25.3.3.1.2"),
        ("hrStorageTable", "1.3.6.1.2.1.25.2.3"),
        ("ifTable (first rows)", "1.3.6.1.2.1.2.2.1.2"),
        ("EtherLike duplex", "1.3.6.1.2.1.10.7.2.1.19"),
        ("LLDP remote table", "1.0.8802.1.1.2.1.4.1"),
        ("Cisco ENVMON", "1.3.6.1.4.1.9.9.13.1"),
        ("Cisco CPU", "1.3.6.1.4.1.9.9.109.1.1.1"),
        ("HP ProCurve sensors", "1.3.6.1.4.1.11.2.14.11.1.2.6"),
        ("ArubaOS-CX ARUBAWIRED", "1.3.6.1.4.1.47196.4.1.1.3.11"),
        ("Juniper jnxOperating", "1.3.6.1.4.1.2636.3.1.13.1"),
        ("TP-Link sysMonitor (CPU, memory)", "1.3.6.1.4.1.11863.6.4"),
        ("BRIDGE-MIB bridge port -> ifIndex", "1.3.6.1.2.1.17.1.4.1.2"),
        ("Q-BRIDGE-MIB MAC table port", "1.3.6.1.2.1.17.7.1.2.2.1.2"),
        ("Q-BRIDGE-MIB MAC table status (3 learned, 4 self, 5 static)", "1.3.6.1.2.1.17.7.1.2.2.1.3"),
        ("Q-BRIDGE-MIB static unicast (configured MACs)", "1.3.6.1.2.1.17.7.1.3.1.1"),
        ("BRIDGE-MIB static table (configured MACs)", "1.3.6.1.2.1.17.5.1.1"),
        ("Q-BRIDGE-MIB VLAN -> FDB id", "1.3.6.1.2.1.17.7.1.4.2.1.3"),
        ("BRIDGE-MIB MAC table port", "1.3.6.1.2.1.17.4.3.1.2"),
        ("ARP table (ipNetToMedia)", "1.3.6.1.2.1.4.22.1.2"),
        ("HPE CPQHLTH", "1.3.6.1.4.1.232.6.1"),
        ("Dell iDRAC status", "1.3.6.1.4.1.674.10892.5.2"),
    ];

    private const int MaxRowsPerTree = 400;
    private const long MaxLogBytes = 60L * 1024 * 1024;

    private readonly IServiceScopeFactory _scopes;
    private readonly NetWatchPaths _paths;
    private readonly ConnectionTestService _tests;
    private readonly CollectorStats _stats;
    private readonly ISecretProtector _protector;
    private readonly SettingsService _settings;

    public DiagnosticBundleService(IServiceScopeFactory scopes, NetWatchPaths paths, ConnectionTestService tests, CollectorStats stats, ISecretProtector protector, SettingsService settings)
    {
        _scopes = scopes;
        _paths = paths;
        _tests = tests;
        _stats = stats;
        _protector = protector;
        _settings = settings;
    }

    [GeneratedRegex("(password|passwd|secret|community|token|passphrase|privatekey|apikey|authkey|privkey)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    /// <summary>Replaces the value of every property whose name looks like a secret (recursively).</summary>
    public static JsonNode? Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (SecretName().IsMatch(key) && o[key] is JsonValue v && v.ToString().Length > 0)
                    {
                        o[key] = "***removed***";
                    }
                    else
                    {
                        Redact(o[key]);
                    }
                }

                break;
            case JsonArray a:
                foreach (var item in a)
                {
                    Redact(item);
                }

                break;
        }

        return node;
    }

    public async Task<byte[]> BuildAsync(IReadOnlyList<int> walkDeviceIds, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Text(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
            Text("README.txt",
                $"HIT SIEM diagnostic bundle, {DateTime.UtcNow:u} UTC, server {Environment.MachineName}, version {typeof(DiagnosticBundleService).Assembly.GetName().Version}.\r\n" +
                "Contains logs (24 h), connection tests, SNMP walks, poll statistics, inventory and configuration.\r\n" +
                "Every password, community, key and token was removed before writing; credential secrets are not included.\r\n");

            // Logs of the last 24 hours (read while Serilog keeps writing).
            long logBytes = 0;
            IEnumerable<FileInfo> logs = Directory.Exists(_paths.LogsDir)
                ? new DirectoryInfo(_paths.LogsDir).EnumerateFiles("*.log").Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-24)).OrderByDescending(f => f.LastWriteTimeUtc)
                : [];
            foreach (var file in logs)
            {
                if (logBytes + file.Length > MaxLogBytes)
                {
                    break;
                }

                using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = zip.CreateEntry($"logs/{file.Name}", CompressionLevel.Optimal).Open();
                await source.CopyToAsync(target, ct);
                logBytes += file.Length;
            }

            Text("connection-tests.json", JsonSerializer.Serialize(_tests.History, json));
            Text("poll-statistics.json", JsonSerializer.Serialize(new
            {
                _stats.StartedUtc,
                _stats.TotalPolls,
                _stats.FailedPolls,
                _stats.Overruns,
                _stats.InFlight,
                _stats.Queued,
                _stats.AvgDurationMs,
                _stats.MaxDurationMs,
            }, json));

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();

            // Configuration: database settings and appsettings.json, secrets removed.
            var settings = new JsonObject();
            foreach (var s in await db.Settings.AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct))
            {
                settings[s.Key] = Redact(JsonNode.Parse(s.ValueJson));
            }

            Text("config/settings.json", settings.ToJsonString(json));
            if (File.Exists(_paths.SettingsFile))
            {
                Text("config/appsettings.json", Redact(JsonNode.Parse(await File.ReadAllTextAsync(_paths.SettingsFile, ct), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))?.ToJsonString(json) ?? "{}");
            }

            var credentials = await db.CredentialProfiles.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name, Kind = c.Kind.ToString(), c.DisplayHint }).ToListAsync(ct);
            Text("config/credentials.json", JsonSerializer.Serialize(credentials.Select(c => new { c.Id, c.Name, c.Kind, Hint = SecretName().IsMatch(c.DisplayHint ?? string.Empty) ? "***" : c.DisplayHint }), json));
            var devices = await db.Devices.AsNoTracking().Include(d => d.Credentials).OrderBy(d => d.Name).ToListAsync(ct);
            Text("inventory.json", JsonSerializer.Serialize(devices.Select(d => new
            {
                d.Id, d.Name, d.IpAddress, Type = d.Type.ToString(), d.Vendor, d.Model, d.Firmware, d.SysObjectId, d.SysDescription, Status = d.Status.ToString(),
                d.LastPollUtc, d.ParentDeviceId, d.BmcAddress, Credentials = d.Credentials.Select(c => new { Purpose = c.Purpose.ToString(), c.CredentialProfileId }),
            }), json));

            // SNMP walks of the selected devices.
            foreach (var id in walkDeviceIds.Distinct().Take(20))
            {
                var device = devices.FirstOrDefault(d => d.Id == id);
                if (device is null)
                {
                    continue;
                }

                Text($"snmp-walks/{Safe(device.Name)}.txt", await WalkAsync(device, db, ct));
            }
        }

        return stream.ToArray();
    }

    private async Task<string> WalkAsync(Core.Entities.Device device, NetWatchDbContext db, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {device.Name} {device.IpAddress} ({device.Vendor} {device.Model}), walked {DateTime.UtcNow:u}");
        var credentialId = device.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.Snmp)?.CredentialProfileId;
        var profile = credentialId is { } cid ? await db.CredentialProfiles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        if (profile is null || !IPAddress.TryParse(device.IpAddress, out var ip))
        {
            sb.AppendLine("# no SNMP credential or no IP address");
            return sb.ToString();
        }

        var m = _settings.Monitoring;
        var client = new SnmpClient(ip, new SnmpCredential(profile.Kind, CredentialSecret.FromJson(_protector.Unprotect(profile.SecretBlob))), m.SnmpTimeoutMs, m.SnmpRetries);
        foreach (var (name, oid) in WalkTrees)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"# --- {name} ({oid})");
            try
            {
                var rows = await client.WalkAsync(oid, ct);
                foreach (var r in rows.Take(MaxRowsPerTree))
                {
                    sb.AppendLine(Format(oid, r.Index, r.Data));
                }

                if (rows.Count > MaxRowsPerTree)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"# ... {rows.Count - MaxRowsPerTree} more rows not included");
                }
            }
            catch (Exception ex) when (ex is NetWatch.Collectors.Snmp.SnmpException or Lextm.SharpSnmpLib.SnmpException or TimeoutException)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"# error: {ex.Message}");
            }
        }

        return sb.ToString();
    }

    /// <summary>One line in "snmpwalk -On" format (the format the parser tests read).</summary>
    public static string Format(string column, string index, ISnmpData data)
    {
        var (type, value) = data switch
        {
            Integer32 i => ("INTEGER", i.ToInt32().ToString(CultureInfo.InvariantCulture)),
            Counter32 c => ("Counter32", c.ToUInt32().ToString(CultureInfo.InvariantCulture)),
            Counter64 c => ("Counter64", c.ToUInt64().ToString(CultureInfo.InvariantCulture)),
            Gauge32 g => ("Gauge32", g.ToUInt32().ToString(CultureInfo.InvariantCulture)),
            TimeTicks t => ("Timeticks", $"({t.ToUInt32().ToString(CultureInfo.InvariantCulture)})"),
            ObjectIdentifier o => ("OID", "." + o.ToString().TrimStart('.')),
            IP a => ("IpAddress", a.ToString()),
            OctetString s when s.GetRaw().Any(b => b < 0x20 && b is not (9 or 10 or 13)) || s.GetRaw().Any(b => b > 0x7E) =>
                ("Hex-STRING", string.Join(' ', s.GetRaw().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))),
            OctetString s => ("STRING", $"\"{s.ToString().Replace("\"", "\\\"", StringComparison.Ordinal)}\""),
            _ => (data.TypeCode.ToString(), data.ToString() ?? string.Empty),
        };
        return $".{column}.{index} = {type}: {value}";
    }

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
