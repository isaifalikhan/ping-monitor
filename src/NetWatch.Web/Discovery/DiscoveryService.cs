using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Icmp;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Snmp;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Credentials;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Discovery;

public enum DiscoveryState
{
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record DiscoveryResultDto(
    string IpAddress,
    double? RttMs,
    bool RespondsToPing,
    int? SnmpCredentialId,
    string? SysName,
    string? SysDescription,
    string? SysObjectId,
    string? Vendor,
    string? Model,
    DeviceType SuggestedType,
    bool AlreadyMonitored);

public sealed record DiscoveryJobDto(Guid Id, string Cidr, DiscoveryState State, int Total, int Scanned, string? Error, DateTime StartedUtc, IReadOnlyList<DiscoveryResultDto> Results);

public sealed record DiscoveryRequest(string? Cidr, IReadOnlyList<int>? CredentialProfileIds);

public sealed record DiscoveryAddItem(string? IpAddress, string? Name, DeviceType Type, int? SnmpCredentialId, int? GroupId);

public sealed record DiscoveryAddRequest(IReadOnlyList<DiscoveryAddItem> Items);

/// <summary>
/// Subnet discovery: ICMP sweep plus an SNMP probe (sysName/sysDescr/sysObjectID) with each selected credential
/// profile. SNMP is tried even when ICMP is filtered. Jobs live in memory for one hour.
/// </summary>
public sealed class DiscoveryService
{
    public const int MaxHosts = 1024;
    private const int Concurrency = 64;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecretProtector _protector;
    private readonly ILogger<DiscoveryService> _logger;
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    private sealed class Job
    {
        public required Guid Id;
        public required string Cidr;
        public required int Total;
        public required DateTime StartedUtc;
        public volatile DiscoveryState State = DiscoveryState.Running;
        public int Scanned;
        public string? Error;
        public readonly ConcurrentDictionary<string, DiscoveryResultDto> Results = new();
        public readonly CancellationTokenSource Cts = new();
    }

    public DiscoveryService(IServiceScopeFactory scopeFactory, ISecretProtector protector, ILogger<DiscoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>Parses "a.b.c.d/nn" or a single IPv4 address into host addresses (network/broadcast excluded).</summary>
    public static bool TryExpand(string? cidr, out List<IPAddress> hosts, out string error)
    {
        hosts = new List<IPAddress>();
        error = string.Empty;
        var text = cidr?.Trim() ?? string.Empty;
        var parts = text.Split('/');
        if (!IPAddress.TryParse(parts[0], out var baseIp) || baseIp.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Enter an IPv4 subnet such as 10.0.1.0/24.";
            return false;
        }

        var prefix = 32;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32))
        {
            error = "The prefix length must be between 0 and 32.";
            return false;
        }

        var size = 1L << (32 - prefix);
        if (size > MaxHosts + 2)
        {
            error = $"Subnets larger than /22 ({MaxHosts} addresses) must be scanned in parts.";
            return false;
        }

        var bytes = baseIp.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = value & mask;
        var first = prefix >= 31 ? network : network + 1;
        var last = prefix >= 31 ? network + (uint)size - 1 : network + (uint)size - 2;
        for (var a = first; a <= last && a >= first; a++)
        {
            hosts.Add(new IPAddress(new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a }));
            if (a == uint.MaxValue)
            {
                break;
            }
        }

        return true;
    }

    public async Task<(Guid? Id, string? Error)> StartAsync(DiscoveryRequest request, CancellationToken ct)
    {
        if (!TryExpand(request.Cidr, out var hosts, out var error))
        {
            return (null, error);
        }

        if (_jobs.Values.Count(j => j.State == DiscoveryState.Running) >= 2)
        {
            return (null, "Two discovery scans are already running. Wait for one to finish.");
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var ids = request.CredentialProfileIds ?? Array.Empty<int>();
        var profiles = await db.CredentialProfiles.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && (p.Kind == CredentialKind.SnmpV2c || p.Kind == CredentialKind.SnmpV3))
            .ToListAsync(ct);
        var credentials = profiles.Select(p => (p.Id, Credential: new SnmpCredential(p.Kind, CredentialSecret.FromJson(_protector.Unprotect(p.SecretBlob))))).ToList();
        var existing = (await db.Devices.AsNoTracking().Select(d => d.IpAddress).ToListAsync(ct)).ToHashSet();

        var job = new Job { Id = Guid.NewGuid(), Cidr = request.Cidr!.Trim(), Total = hosts.Count, StartedUtc = DateTime.UtcNow };
        _jobs[job.Id] = job;
        Cleanup();

        _ = Task.Run(() => RunAsync(job, hosts, credentials, existing), CancellationToken.None);
        return (job.Id, null);
    }

    private async Task RunAsync(Job job, List<IPAddress> hosts, List<(int Id, SnmpCredential Credential)> credentials, HashSet<string> existing)
    {
        using var gate = new SemaphoreSlim(Concurrency);
        try
        {
            var tasks = hosts.Select(async ip =>
            {
                await gate.WaitAsync(job.Cts.Token);
                try
                {
                    await ProbeAsync(job, ip, credentials, existing);
                }
                finally
                {
                    Interlocked.Increment(ref job.Scanned);
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);
            job.State = DiscoveryState.Completed;
            _logger.LogInformation("Discovery of {Cidr} finished: {Found} host(s) found", job.Cidr, job.Results.Count);
        }
        catch (OperationCanceledException)
        {
            job.State = DiscoveryState.Cancelled;
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.State = DiscoveryState.Failed;
            _logger.LogError(ex, "Discovery of {Cidr} failed", job.Cidr);
        }
    }

    private static async Task ProbeAsync(Job job, IPAddress ip, List<(int Id, SnmpCredential Credential)> credentials, HashSet<string> existing)
    {
        var ct = job.Cts.Token;
        var ping = await PingProbe.ProbeAsync(ip, 1, 800, ct);
        DeviceInventoryInfo? info = null;
        int? credentialId = null;
        foreach (var (id, credential) in credentials)
        {
            try
            {
                var client = new SnmpClient(ip, credential, 1500, 0);
                var sys = await client.GetAsync([Oids.SysName, Oids.SysDescr, Oids.SysObjectId], ct);
                if (sys.Count == 0)
                {
                    continue;
                }

                var objectId = SnmpValues.Text(sys.GetValueOrDefault(Oids.SysObjectId))?.TrimStart('.');
                info = new DeviceInventoryInfo(VendorCatalog.VendorFromSysObjectId(objectId), null, null, null, objectId,
                    SnmpValues.Text(sys.GetValueOrDefault(Oids.SysName)), SnmpValues.Text(sys.GetValueOrDefault(Oids.SysDescr)), null);
                credentialId = id;
                break;
            }
            catch (Exception ex) when (ex is SnmpException or TimeoutException)
            {
                // Not answering with this credential; try the next one.
            }
        }

        if (!ping.Reachable && info is null)
        {
            return;
        }

        var text = ip.ToString();
        job.Results[text] = new DiscoveryResultDto(
            text, ping.AvgRttMs, ping.Reachable, credentialId, info?.SysName, info?.SysDescription, info?.SysObjectId, info?.Vendor, info?.Model,
            VendorCatalog.SuggestType(info?.Vendor, info?.SysDescription), existing.Contains(text));
    }

    public DiscoveryJobDto? Get(Guid id) =>
        _jobs.TryGetValue(id, out var j)
            ? new DiscoveryJobDto(j.Id, j.Cidr, j.State, j.Total, Volatile.Read(ref j.Scanned), j.Error, j.StartedUtc,
                j.Results.Values.OrderBy(r => IPAddress.Parse(r.IpAddress).GetAddressBytes(), ByteArrayComparer.Instance).ToList())
            : null;

    public void MarkAdded(Guid id, IEnumerable<string> ips)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return;
        }

        foreach (var ip in ips)
        {
            if (job.Results.TryGetValue(ip, out var r))
            {
                job.Results[ip] = r with { AlreadyMonitored = true };
            }
        }
    }

    private void Cleanup()
    {
        foreach (var (id, job) in _jobs.Where(kv => DateTime.UtcNow - kv.Value.StartedUtc > TimeSpan.FromHours(1)).ToList())
        {
            job.Cts.Cancel();
            _jobs.TryRemove(id, out _);
        }
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            for (var i = 0; i < Math.Min(x!.Length, y!.Length); i++)
            {
                var c = x[i].CompareTo(y[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}

public static class DiscoveryEndpoints
{
    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/discovery").RequireAuthorization(Policies.Admin);
        admin.MapPost("/", StartAsync);
        admin.MapGet("/{id:guid}", (Guid id, DiscoveryService svc) => svc.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());
        admin.MapPost("/{id:guid}/add", AddAsync);
    }

    private static async Task<IResult> StartAsync(DiscoveryRequest request, HttpContext http, DiscoveryService svc, AuditWriter audit, CancellationToken ct)
    {
        var (id, error) = await svc.StartAsync(request, ct);
        if (error is not null)
        {
            return HttpExtensions.Error(error);
        }

        await audit.WriteAsync(http.Actor(), "DiscoveryStarted", "Discovery", id.ToString(), request.Cidr);
        return Results.Ok(new { id });
    }

    private static async Task<IResult> AddAsync(Guid id, DiscoveryAddRequest request, HttpContext http, DiscoveryService svc, NetWatchDbContext db, SettingsService settings, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        if (request.Items is not { Count: > 0 })
        {
            return HttpExtensions.Error("Select at least one host.");
        }

        var existing = (await db.Devices.Select(d => d.IpAddress).ToListAsync(ct)).ToHashSet();
        var validCredentials = (await db.CredentialProfiles.Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var validGroups = (await db.Groups.Select(g => g.Id).ToListAsync(ct)).ToHashSet();
        var now = DateTime.UtcNow;
        var added = new List<string>();
        foreach (var item in request.Items)
        {
            if (!IPAddress.TryParse(item.IpAddress, out var ip) || !existing.Add(ip.ToString()))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(item.Name) ? ip.ToString() : item.Name.Trim();
            db.Devices.Add(new Device
            {
                Name = name.Length > 200 ? name[..200] : name,
                IpAddress = ip.ToString(),
                Type = item.Type,
                Os = item.Type is DeviceType.Switch or DeviceType.Router or DeviceType.Firewall ? OsKind.NetworkOs : OsKind.Unknown,
                GroupId = item.GroupId is { } g && validGroups.Contains(g) ? g : null,
                PollIntervalSec = settings.Monitoring.DefaultPollIntervalSec,
                Status = DeviceStatus.Unknown,
                StatusSinceUtc = now,
                CreatedUtc = now,
                UpdatedUtc = now,
                Credentials = item.SnmpCredentialId is { } c && validCredentials.Contains(c)
                    ? [new DeviceCredential { Purpose = CredentialPurpose.Snmp, CredentialProfileId = c }]
                    : [],
            });
            added.Add(ip.ToString());
        }

        await db.SaveChangesAsync(ct);
        svc.MarkAdded(id, added);
        if (added.Count > 0)
        {
            notifier.NotifyInventoryChanged();
        }

        await audit.WriteAsync(http.Actor(), "DevicesDiscovered", "Device", null, $"{added.Count} device(s) added: {string.Join(", ", added.Take(50))}");
        return Results.Ok(new { created = added.Count });
    }
}
