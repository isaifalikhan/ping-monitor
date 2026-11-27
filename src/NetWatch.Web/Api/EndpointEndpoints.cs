using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Collectors.DomainControllers;
using NetWatch.Collectors.Endpoints;
using NetWatch.Collectors.Inventory;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Api;

/// <summary>
/// Endpoint inventory: the anonymous report route the GPO script posts to (authenticated by enrollment key /
/// machine token), the inventory pages, and the deployment kit (Admin).
/// </summary>
public static class EndpointEndpoints
{
    public const string ReportPath = "/api/endpoints/report";
    public const string ReportRateLimit = "endpoint-report";
    private const long MaxCompressedBytes = 8L * 1024 * 1024;
    private const long MaxJsonBytes = 64L * 1024 * 1024;

    public static void MapEndpointEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(ReportPath, ReportAsync).AllowAnonymous().RequireRateLimiting(ReportRateLimit).DisableAntiforgery();

        var read = app.MapGroup("/api/endpoints").RequireAuthorization(Policies.Viewer);
        read.MapGet("/", List);
        read.MapGet("/{id:int}", Detail);
        read.MapGet("/{id:int}/changes", (int id, int? days, EndpointStore store) =>
            store.Get(id) is null ? Results.NotFound() : Results.Ok(store.Changes(id, DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 90, 1, 3650)), null, 2000).Select(ChangeDto.From)));
        read.MapGet("/{id:int}/events", EventsAsync);
        read.MapGet("/{id:int}/actions", (int id, EndpointStore store) =>
            store.Get(id) is null ? Results.NotFound() : Results.Ok(store.ActionsFor(id, 200).Select(ActionDto.From)));
        read.MapGet("/by-device/{deviceId:int}", (int deviceId, EndpointStore store) =>
            store.ForDevice(deviceId) is { } row ? Results.Ok(new { id = row.Id }) : Results.NotFound());
        read.MapGet("/software", (string? q, EndpointStore store) =>
            Results.Ok(store.SoftwareSummary(q, 2000).Select(s => new { name = s.Name, publisher = s.Publisher, machines = s.Machines, versions = s.Versions.Split(',', StringSplitOptions.RemoveEmptyEntries).Distinct().Order().ToList() })));
        read.MapGet("/software/machines", SoftwareMachines);
        read.MapGet("/processes", SearchProcesses);
        read.MapGet("/files", SearchFiles);
        read.MapGet("/{id:int}/files", (int id, string? q, string? folder, EndpointStore store) =>
            store.Get(id) is null ? Results.NotFound() : Results.Ok(store.Files(id, q, folder, 5000).Select(FileDto.From)));
        read.MapGet("/changes", (int? days, string? kind, EndpointStore store) =>
            Results.Ok(store.Changes(null, DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 3650)), string.IsNullOrWhiteSpace(kind) ? null : kind, 3000).Select(ChangeDto.From)));

        var admin = app.MapGroup("/api/endpoints").RequireAuthorization(Policies.Admin);
        admin.MapGet("/deployment", Deployment);
        admin.MapGet("/kit", KitAsync);
        admin.MapPost("/enrollment-key/rotate", RotateAsync);
        admin.MapPost("/{id:int}/reset", ResetAsync);
        admin.MapDelete("/{id:int}", DeleteAsync);
        admin.MapPost("/{id:int}/quarantine", QuarantineAsync);
        admin.MapPost("/{id:int}/actions/{actionId:int}/cancel", CancelActionAsync);
    }

    // ------------------------------------------------------------------ report (anonymous, machine-authenticated)

    public sealed record FileDto(int EndpointId, string Path, string Name, string Ext, string? User, string? Folder, long Size, DateTime? ModifiedUtc, DateTime? CreatedUtc, string? Sha256)
    {
        public static FileDto From(EndpointFileRow f) =>
            new(f.EndpointId, f.Path, System.IO.Path.GetFileName(f.Path.Replace('/', '\\').Split('\\')[^1]), EndpointStore.Extension(f.Path), f.User, f.Folder, f.Size, f.ModifiedUtc, f.CreatedUtc, f.Sha256);
    }

    private static IResult SearchFiles(string? q, string? ext, string? sha256, double? minMb, string? folder, EndpointStore store)
    {
        if (string.IsNullOrWhiteSpace(q) && string.IsNullOrWhiteSpace(ext) && string.IsNullOrWhiteSpace(sha256) && minMb is null)
        {
            return HttpExtensions.Error("Enter a name, an extension, a SHA-256 or a minimum size.");
        }

        if (!string.IsNullOrWhiteSpace(sha256) && !System.Text.RegularExpressions.Regex.IsMatch(sha256.Trim(), "^[0-9A-Fa-f]{64}$"))
        {
            return HttpExtensions.Error("A SHA-256 has 64 hexadecimal characters.");
        }

        var rows = store.All().ToDictionary(r => r.Id);
        var minBytes = minMb is { } mb ? (long)(Math.Max(0, mb) * 1024 * 1024) : (long?)null;
        return Results.Ok(store.SearchFiles(q, ext, sha256, minBytes, folder, 2000).Where(f => rows.ContainsKey(f.EndpointId))
            .Select(f => new { file = FileDto.From(f), machine = rows[f.EndpointId].Name, lastSeenUtc = rows[f.EndpointId].LastSeenUtc }));
    }

    /// <summary>Endpoint settings the script applies from its next run on (so most changes need no new kit).</summary>
    private static object ScriptSettings(EndpointSettings s) => new
    {
        collectCommandLines = s.CollectCommandLines,
        collectConnections = s.CollectConnections,
        collectFiles = s.CollectFiles,
        maxFiles = s.MaxFilesPerMachine,
        hashMaxMb = s.HashMaxMb,
    };

    private static async Task<IResult> ReportAsync(HttpContext http, EndpointIngestService ingest, SettingsService settings, CancellationToken ct)
    {
        var machine = Header(http, "X-HitSiem-Machine");
        var token = Header(http, "X-HitSiem-Token");
        var key = Header(http, "X-HitSiem-Key");
        if (token is null && key is null)
        {
            return HttpExtensions.Error("Missing enrollment key or machine token.", StatusCodes.Status401Unauthorized);
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxCompressedBytes;
        }

        EndpointReport? report;
        try
        {
            var gzip = string.Equals(Header(http, "Content-Encoding"), "gzip", StringComparison.OrdinalIgnoreCase);
            await using var source = gzip ? new GZipStream(http.Request.Body, CompressionMode.Decompress) : http.Request.Body;
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await source.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxJsonBytes)
                {
                    return HttpExtensions.Error("Report too large.", StatusCodes.Status413PayloadTooLarge);
                }
            }

            buffer.Position = 0;
            report = JsonSerializer.Deserialize<EndpointReport>(buffer, EndpointStore.Json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return HttpExtensions.Error("The report is not valid JSON: " + ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            return HttpExtensions.Error(ex.Message, ex.StatusCode);
        }

        if (report is null)
        {
            return HttpExtensions.Error("Empty report.");
        }

        var ip = http.Connection.RemoteIpAddress is { } a ? (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString() : null;
        var result = await ingest.IngestAsync(new IngestRequest(machine, token, key, ip, report), ct);
        var status = result.Status switch
        {
            IngestStatus.Ok => StatusCodes.Status200OK,
            IngestStatus.Unauthorized => StatusCodes.Status401Unauthorized,
            IngestStatus.Conflict => StatusCodes.Status409Conflict,
            IngestStatus.TooSoon => StatusCodes.Status429TooManyRequests,
            IngestStatus.Disabled => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        return status == StatusCodes.Status200OK
            ? Results.Ok(new
            {
                message = result.Message,
                token = result.NewToken,
                endpointId = result.EndpointId,
                changes = result.Changes,
                settings = ScriptSettings(settings.Endpoints),
                actions = (result.Actions ?? []).Select(a => new { id = a.Id, kind = a.Kind, path = a.TargetPath }),
            })
            : HttpExtensions.Error(result.Message, status);
    }

    private static string? Header(HttpContext http, string name) =>
        http.Request.Headers.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString().Trim() : null;

    // ------------------------------------------------------------------ reads

    public sealed record EndpointListItem(
        int Id, int? DeviceId, string Name, string? Domain, string? Ip, string? Os, string? OsBuild, string? Model, string? Serial, string? User,
        DateTime FirstSeenUtc, DateTime LastSeenUtc, bool Stale, string? FirewallOff, string? AvProblem, int? MissingUpdates, DateTime? LastPatchUtc,
        bool? PendingReboot, double? WorstDiskPct, int ProcessCount, int SoftwareCount, string? Errors, string? ScriptVersion, Severity? WorstAlert, bool Enrolled, int? FileCount);

    private static EndpointListItem Item(EndpointRow r, DateTime now, int staleHours, IActiveAlertIndex alerts) => new(
        r.Id, r.DeviceId, r.Name, r.Domain, r.LastIp, r.Os, r.OsBuild, r.Model, r.Serial, r.User, r.FirstSeenUtc, r.LastSeenUtc,
        now - r.LastSeenUtc > TimeSpan.FromHours(staleHours), r.FirewallOff is { Length: > 0 } ? r.FirewallOff : null, r.AvProblem, r.MissingUpdates,
        r.LastPatchUtc, r.PendingReboot, r.WorstDiskPct, r.ProcessCount, r.SoftwareCount, r.Errors, r.ScriptVersion,
        r.DeviceId is { } d ? alerts.GetWorstSeverity(d) : null, r.TokenHash is not null, r.FileCount);

    private static IResult List(EndpointStore store, SettingsService settings, IActiveAlertIndex alerts)
    {
        var now = DateTime.UtcNow;
        var stale = settings.Endpoints.StaleHours;
        return Results.Ok(store.All().Select(r => Item(r, now, stale, alerts)));
    }

    private static IResult Detail(int id, EndpointStore store, SettingsService settings, IActiveAlertIndex alerts, InventoryCache inventory)
    {
        if (store.Get(id) is not { } row)
        {
            return Results.NotFound();
        }

        var device = row.DeviceId is { } d ? inventory.GetSnapshot(d) : null;
        return Results.Ok(new
        {
            endpoint = Item(row, DateTime.UtcNow, settings.Endpoints.StaleHours, alerts),
            device = device is null ? null : new { device.Id, device.Name, device.IpAddress, device.Type },
            report = store.Report(id),
        });
    }

    private static async Task<IResult> EventsAsync(int id, int? limit, EndpointStore store, EventsDbContext events, CancellationToken ct)
    {
        if (store.Get(id) is not { DeviceId: { } deviceId })
        {
            return Results.NotFound();
        }

        var take = Math.Clamp(limit ?? 300, 1, 2000);
        var list = await events.WindowsEvents.AsNoTracking().Where(e => e.DeviceId == deviceId)
            .OrderByDescending(e => e.TimestampUtc).Take(take)
            .Select(e => new { e.Id, e.TimestampUtc, e.LogName, e.Provider, e.EventId, e.Level, e.Message })
            .ToListAsync(ct);
        return Results.Ok(list);
    }

    private static IResult SoftwareMachines(string? name, EndpointStore store, SettingsService settings)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return HttpExtensions.Error("Name required.");
        }

        var rows = store.All().ToDictionary(r => r.Id);
        var now = DateTime.UtcNow;
        var staleHours = settings.Endpoints.StaleHours;
        return Results.Ok(store.SoftwareMachines(name.Trim()).Where(m => rows.ContainsKey(m.EndpointId)).Select(m =>
        {
            var r = rows[m.EndpointId];
            return new { id = r.Id, name = r.Name, ip = r.LastIp, user = r.User, os = r.Os, version = m.Version, lastSeenUtc = r.LastSeenUtc, stale = now - r.LastSeenUtc > TimeSpan.FromHours(staleHours) };
        }));
    }

    private static IResult SearchProcesses(string? q, EndpointStore store)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            return HttpExtensions.Error("Enter at least 2 characters.");
        }

        var rows = store.All().ToDictionary(r => r.Id);
        return Results.Ok(store.SearchProcesses(q, 2000).Where(p => rows.ContainsKey(p.EndpointId)).Select(p => new
        {
            endpointId = p.EndpointId,
            machine = rows[p.EndpointId].Name,
            name = p.Name,
            path = p.Path,
            user = p.User,
            pid = p.Pid,
            commandLine = p.CommandLine,
            lastSeenUtc = rows[p.EndpointId].LastSeenUtc,
        }));
    }

    public sealed record ChangeDto(long Id, int EndpointId, DateTime TimestampUtc, string Kind, string Item, string? Detail)
    {
        public static ChangeDto From(EndpointChange c) => new(c.Id, c.EndpointId, c.TimestampUtc, c.Kind, c.Item, c.Detail);
    }

    // ------------------------------------------------------------------ admin: deployment

    private static IResult Deployment(EndpointStore store, SettingsService settings, EndpointIngestService ingest, ForwardedEventsReader forwarded, ServerCertificateInfo certificate)
    {
        var all = store.All();
        var now = DateTime.UtcNow;
        var s = settings.Endpoints;
        return Results.Ok(new
        {
            settings = s,
            enrollmentKey = store.EnrollmentKey(),
            certificateThumbprint = certificate.Thumbprint,
            certificateSubject = certificate.Subject,
            machines = all.Count,
            reporting = all.Count(r => now - r.LastSeenUtc <= TimeSpan.FromHours(s.StaleHours)),
            stale = all.Count(r => now - r.LastSeenUtc > TimeSpan.FromHours(s.StaleHours)),
            reportsAccepted = ingest.ReportsAccepted,
            reportsRefused = ingest.ReportsRefused,
            lastReportUtc = ingest.LastReportUtc,
            forwardedLastReadUtc = forwarded.LastReadUtc,
            forwardedError = forwarded.LastError,
            forwardedEndpointEvents = forwarded.EndpointEventsRead,
            scriptSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(EndpointKit.Script())),
        });
    }

    private static async Task<IResult> KitAsync(string? server, HttpContext http, EndpointStore store, SettingsService settings, ServerCertificateInfo certificate, AuditWriter audit)
    {
        var url = string.IsNullOrWhiteSpace(server) ? $"https://{http.Request.Host}" : server.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath.Length > 1 || !string.IsNullOrEmpty(uri.Query))
        {
            return HttpExtensions.Error("Enter the HIT SIEM address the machines use, e.g. https://hitsiem.corp.local:8443");
        }

        var zip = EndpointKit.Build(uri.GetLeftPart(UriPartial.Authority), store.EnrollmentKey(), certificate.Thumbprint, settings.Endpoints);
        await audit.WriteAsync(http.Actor(), "EndpointKitDownloaded", "Endpoints", null, uri.GetLeftPart(UriPartial.Authority));
        return Results.File(zip, "application/zip", "hit-siem-endpoint-kit.zip");
    }

    private static async Task<IResult> RotateAsync(HttpContext http, EndpointStore store, AuditWriter audit)
    {
        store.RotateEnrollmentKey();
        await audit.WriteAsync(http.Actor(), "EndpointKeyRotated", "Endpoints", null, "New enrollment key; enrolled machines keep their token");
        return Results.Ok(new { enrollmentKey = store.EnrollmentKey() });
    }

    private static async Task<IResult> ResetAsync(int id, HttpContext http, EndpointStore store, AuditWriter audit)
    {
        if (store.Get(id) is not { } row)
        {
            return Results.NotFound();
        }

        await store.ResetTokenAsync(id);
        await audit.WriteAsync(http.Actor(), "EndpointEnrollmentReset", "Endpoint", id.Inv(), row.Name);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(int id, bool? deleteDevice, HttpContext http, EndpointStore store, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        if (store.Get(id) is not { } row)
        {
            return Results.NotFound();
        }

        await store.DeleteAsync(id);
        var deviceRemoved = false;
        if (deleteDevice != false && row.DeviceId is { } deviceId &&
            await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.Type == DeviceType.Workstation && d.Tags.Any(t => t.Tag == EndpointIngestService.EndpointTag), ct) is { } device)
        {
            db.Devices.Remove(device);
            await db.SaveChangesAsync(ct);
            notifier.NotifyInventoryChanged();
            deviceRemoved = true;
        }

        await audit.WriteAsync(http.Actor(), "EndpointDeleted", "Endpoint", id.Inv(), row.Name + (deviceRemoved ? " (and its workstation device)" : string.Empty));
        return Results.NoContent();
    }

    public sealed record ActionDto(int Id, string Kind, string Path, string Status, string RequestedBy, DateTime RequestedUtc, DateTime? SentUtc, bool? Ok, string? Result, DateTime? ResultUtc)
    {
        public static ActionDto From(EndpointAction a) => new(a.Id, a.Kind, a.TargetPath, a.Status, a.RequestedBy, a.RequestedUtc, a.SentUtc, a.Ok, a.Result, a.ResultUtc);
    }

    public sealed record QuarantineInput(string? Path);

    private static async Task<IResult> QuarantineAsync(int id, QuarantineInput input, HttpContext http, EndpointStore store, SettingsService settings, AuditWriter audit, CancellationToken ct)
    {
        if (store.Get(id) is not { } row)
        {
            return Results.NotFound();
        }

        if (!settings.Endpoints.AllowRemediation)
        {
            return HttpExtensions.Error("Remediation is turned off. Turn on \"Allow file quarantine actions\" under Endpoints → Deployment first.", StatusCodes.Status403Forbidden);
        }

        var path = input.Path?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return HttpExtensions.Error("No file path.");
        }

        // Only a file HIT SIEM actually inventoried for this machine, and never the hosts/system file.
        var file = store.Files(id, null, null, 50000).FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return HttpExtensions.Error("That file is not in this machine's inventory (refresh the file list first).");
        }

        if (string.Equals(file.Folder, "System", StringComparison.OrdinalIgnoreCase))
        {
            return HttpExtensions.Error("System files cannot be quarantined.");
        }

        var actionId = await store.QueueActionAsync(id, "quarantine", file.Path, http.User.Username() ?? "admin", DateTime.UtcNow);
        await audit.WriteAsync(http.Actor(), "EndpointQuarantineQueued", "Endpoint", id.Inv(), $"{row.Name}: {file.Path}");
        return Results.Ok(new { id = actionId, message = "Quarantine queued. The machine applies it at its next check-in." });
    }

    private static async Task<IResult> CancelActionAsync(int id, int actionId, HttpContext http, EndpointStore store, AuditWriter audit)
    {
        if (store.Get(id) is not { } row)
        {
            return Results.NotFound();
        }

        if (!await store.CancelActionAsync(id, actionId))
        {
            return HttpExtensions.Error("This action was already sent to the machine and can no longer be cancelled.");
        }

        await audit.WriteAsync(http.Actor(), "EndpointActionCancelled", "Endpoint", id.Inv(), $"{row.Name}: action {actionId}");
        return Results.NoContent();
    }
}

/// <summary>The deployment kit: script, configuration, WEF subscription and instructions, as one zip.</summary>
public static class EndpointKit
{
    public static byte[] Script() => Resource("HitSiemInventory.ps1");

    public static byte[] Build(string server, string enrollmentKey, string? thumbprint, EndpointSettings settings)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "HitSiemInventory.ps1", Script());
            var config = JsonSerializer.SerializeToUtf8Bytes(new
            {
                server,
                thumbprint = thumbprint ?? string.Empty,
                enrollmentKey,
                collectCommandLines = settings.CollectCommandLines,
                collectConnections = settings.CollectConnections,
                collectFiles = settings.CollectFiles,
                maxFiles = settings.MaxFilesPerMachine,
                hashMaxMb = settings.HashMaxMb,
            }, new JsonSerializerOptions { WriteIndented = true });
            Add(zip, "hitsiem-endpoint.json", config);
            Add(zip, "HitSiem-Endpoints-Subscription.xml", Encoding.UTF8.GetBytes(SubscriptionXml()));
            Add(zip, "HitSiem-Endpoints-Query.xml", Encoding.UTF8.GetBytes(QueryXml()));
            var readme = Encoding.UTF8.GetString(Resource("README.txt"))
                .Replace("{SERVER}", server, StringComparison.Ordinal)
                .Replace("{INTERVAL}", settings.IntervalMin.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{HOST}", new Uri(server).Host, StringComparison.Ordinal);
            Add(zip, "README.txt", Encoding.UTF8.GetBytes(readme.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)));
        }

        return ms.ToArray();
    }

    /// <summary>XPath query of the endpoint events (also usable in Event Viewer → Custom view → XML).</summary>
    public static string QueryXml()
    {
        var sb = new StringBuilder("<QueryList>\n  <Query Id=\"0\">\n");
        foreach (var (channel, ids) in EndpointEventCatalog.Events)
        {
            sb.Append(CultureInfo.InvariantCulture, $"    <Select Path=\"{channel}\">*[System[({string.Join(" or ", ids.Select(i => "EventID=" + i.ToString(CultureInfo.InvariantCulture)))})]]</Select>\n");
        }

        return sb.Append("  </Query>\n</QueryList>\n").ToString();
    }

    /// <summary>Source-initiated WEF subscription for all domain computers (wecutil cs on the HIT SIEM server).</summary>
    public static string SubscriptionXml() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Subscription xmlns="http://schemas.microsoft.com/2006/03/windows/events/subscription">
          <SubscriptionId>HIT-SIEM-Endpoints</SubscriptionId>
          <SubscriptionType>SourceInitiated</SubscriptionType>
          <Description>HIT SIEM endpoint events: process starts, failed logons, local accounts and groups, services, USB devices, software installs, Defender, Windows Firewall, log clearing.</Description>
          <Enabled>true</Enabled>
          <Uri>http://schemas.microsoft.com/wbem/wsman/1/windows/EventLog</Uri>
          <ConfigurationMode>Custom</ConfigurationMode>
          <Delivery Mode="Push">
            <Batching>
              <MaxItems>100</MaxItems>
              <MaxLatencyTime>60000</MaxLatencyTime>
            </Batching>
            <PushSettings>
              <Heartbeat Interval="3600000"/>
            </PushSettings>
          </Delivery>
          <Query><![CDATA[{QueryXml()}]]></Query>
          <ReadExistingEvents>false</ReadExistingEvents>
          <TransportName>HTTP</TransportName>
          <ContentFormat>RenderedText</ContentFormat>
          <Locale Language="en-US"/>
          <LogFile>ForwardedEvents</LogFile>
          <PublisherName>Microsoft-Windows-EventCollector</PublisherName>
          <AllowedSourceNonDomainComputers></AllowedSourceNonDomainComputers>
          <AllowedSourceDomainComputers>O:NSG:BAD:P(A;;GA;;;DC)(A;;GA;;;DD)S:</AllowedSourceDomainComputers>
        </Subscription>
        """;

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(content);
    }

    private static byte[] Resource(string name)
    {
        using var s = typeof(EndpointKit).Assembly.GetManifestResourceStream("EndpointKit." + name)
            ?? throw new InvalidOperationException($"Embedded kit file {name} is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
