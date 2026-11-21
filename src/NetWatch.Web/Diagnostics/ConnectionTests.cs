using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetWatch.Alerts.Notifications;
using NetWatch.Collectors.Hardware;
using NetWatch.Collectors.Icmp;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Servers.Windows;
using NetWatch.Collectors.Snmp;
using NetWatch.Collectors.Vmware;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Credentials;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Auth;

namespace NetWatch.Web.Diagnostics;

/// <summary>What a connection test checks.</summary>
public enum ConnectionTestKind
{
    Ping,
    Snmp,
    WinRm,
    EventLog,
    Ssh,
    VCenter,
    Redfish,
    Ldap,
    Smtp,
    Receivers,
}

/// <param name="Address">Target host; when empty the device's address (or BMC address for Redfish) is used.</param>
/// <param name="Username">LDAP test only: the account to bind as (never stored).</param>
/// <param name="Minutes">Receivers test: look back this many minutes (default 15).</param>
public sealed record ConnectionTestRequest(ConnectionTestKind Kind, int? DeviceId, string? Address, int? CredentialId, string? Username = null, string? Password = null, int? Minutes = null);

public sealed record ConnectionTestStep(string Name, bool Ok, string Detail);

public sealed record ConnectionTestResult(
    Guid Id, ConnectionTestKind Kind, string Target, bool Ok, double DurationMs, string Summary, IReadOnlyList<ConnectionTestStep> Steps, string? Error,
    DateTime TimestampUtc, string? RequestedBy);

/// <summary>
/// Pilot connection tests: run one check against one device with one credential and report every step with the
/// exact error text, so problems inside the air gap can be fixed (or reported with the diagnostic bundle).
/// Results of the last 100 tests are kept in memory for the bundle.
/// </summary>
public sealed class ConnectionTestService
{
    private readonly ConcurrentQueue<ConnectionTestResult> _history = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly IServerCollector _servers;
    private readonly IVmwareCollector _vmware;
    private readonly IHardwareCollector _hardware;
    private readonly LdapAuthenticator _ldap;
    private readonly NotificationDispatcher _notifications;
    private readonly SettingsService _settings;
    private readonly ISecretProtector _protector;

    public ConnectionTestService(IServiceScopeFactory scopes, IServerCollector servers, IVmwareCollector vmware, IHardwareCollector hardware, LdapAuthenticator ldap,
        NotificationDispatcher notifications, SettingsService settings, ISecretProtector protector)
    {
        _scopes = scopes;
        _servers = servers;
        _vmware = vmware;
        _hardware = hardware;
        _ldap = ldap;
        _notifications = notifications;
        _settings = settings;
        _protector = protector;
    }

    public IReadOnlyList<ConnectionTestResult> History => _history.Reverse().ToList();

    public async Task<ConnectionTestResult> RunAsync(ConnectionTestRequest request, string? requestedBy, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var steps = new List<ConnectionTestStep>();
        string target = request.Address?.Trim() ?? string.Empty;
        string? error = null;
        string summary;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var device = request.DeviceId is { } id ? await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) : null;
            if (target.Length == 0)
            {
                target = request.Kind == ConnectionTestKind.Redfish ? device?.BmcAddress ?? string.Empty : device?.IpAddress ?? string.Empty;
            }

            if (target.Length == 0 && request.Kind is not (ConnectionTestKind.Ldap or ConnectionTestKind.Smtp))
            {
                throw new ArgumentException("Choose a device or enter an address.");
            }

            var (kind, secret) = request.CredentialId is { } cid ? await CredentialAsync(db, cid, ct) : (null, null);
            summary = request.Kind switch
            {
                ConnectionTestKind.Ping => await PingAsync(target, steps, ct),
                ConnectionTestKind.Snmp => await SnmpAsync(target, Need(kind, secret, CredentialKind.SnmpV2c, CredentialKind.SnmpV3), steps, ct),
                ConnectionTestKind.WinRm or ConnectionTestKind.Ssh => await ServerAsync(target, Need(kind, secret, request.Kind == ConnectionTestKind.Ssh ? [CredentialKind.Ssh] : [CredentialKind.Windows]), steps, ct),
                ConnectionTestKind.EventLog => await EventLogAsync(target, Need(kind, secret, CredentialKind.Windows).Secret, steps, ct),
                ConnectionTestKind.VCenter => await VcenterAsync(target, Need(kind, secret, CredentialKind.VCenter), steps, ct),
                ConnectionTestKind.Redfish => await RedfishAsync(target, Need(kind, secret, CredentialKind.Redfish), steps, ct),
                ConnectionTestKind.Ldap => await LdapAsync(request, steps, ct),
                ConnectionTestKind.Smtp => await SmtpAsync(steps, ct),
                ConnectionTestKind.Receivers => await ReceiversAsync(scope.ServiceProvider.GetRequiredService<EventsDbContext>(), device, target, Math.Clamp(request.Minutes ?? 15, 1, 1440), steps, ct),
                _ => throw new ArgumentException("Unknown test."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = Describe(ex);
            summary = "Failed";
        }

        var ok = error is null && steps.Count > 0 && steps.All(s => s.Ok);
        var result = new ConnectionTestResult(Guid.NewGuid(), request.Kind, target, ok, Math.Round(watch.Elapsed.TotalMilliseconds), summary, steps, error ?? steps.FirstOrDefault(s => !s.Ok)?.Detail,
            DateTime.UtcNow, requestedBy);
        _history.Enqueue(result);
        while (_history.Count > 100 && _history.TryDequeue(out _))
        {
        }

        return result;
    }

    // ------------------------------------------------------------------ tests

    private static async Task<string> PingAsync(string target, List<ConnectionTestStep> steps, CancellationToken ct)
    {
        var ip = await ResolveAsync(target, steps, ct);
        var p = await PingProbe.ProbeAsync(ip, 4, 1000, ct);
        steps.Add(new ConnectionTestStep("ICMP echo (4)", p.Reachable,
            p.Reachable ? string.Create(CultureInfo.InvariantCulture, $"{p.AvgRttMs:0.#} ms average, {p.LossPct:0} % loss") : "no reply (host down, or ICMP blocked by a firewall)"));
        return p.Reachable ? "Reachable" : "No reply";
    }

    private async Task<string> SnmpAsync(string target, (CredentialKind Kind, CredentialSecret Secret) credential, List<ConnectionTestStep> steps, CancellationToken ct)
    {
        var ip = await ResolveAsync(target, steps, ct);
        var m = _settings.Monitoring;
        var client = new SnmpClient(ip, new SnmpCredential(credential.Kind, credential.Secret), m.SnmpTimeoutMs, m.SnmpRetries);
        try
        {
            var info = await SystemInfoProbe.ProbeAsync(client, ct);
            steps.Add(new ConnectionTestStep($"SNMP {(credential.Kind == CredentialKind.SnmpV3 ? "v3" : "v2c")} system group", true,
                $"{info.SysName} · {info.SysDescription} · sysObjectID {info.SysObjectId} ({info.Vendor ?? "unknown vendor"})"));
        }
        catch (Exception ex) when (ex is SnmpException or TimeoutException)
        {
            steps.Add(new ConnectionTestStep("SNMP system group", false, Hint(ex.Message)));
            return "No SNMP answer";
        }

        var names = await InterfaceCollector.SafeWalk(client, Oids.IfDescr, ct);
        steps.Add(new ConnectionTestStep("IF-MIB walk (ifDescr)", names.Count > 0,
            names.Count > 0 ? $"{names.Count} interfaces, e.g. {string.Join(", ", names.Values.Take(5).Select(SnmpValues.Text))}" : "the interface table is empty or not in the SNMP view"));
        return names.Count > 0 ? $"SNMP OK, {names.Count} interfaces" : "SNMP answers, no interfaces";

        static string Hint(string message) => message.Contains("timed out", StringComparison.OrdinalIgnoreCase) || message.Contains("Timeout", StringComparison.Ordinal)
            ? $"{message} — check the community/v3 user, the switch's SNMP ACL (the NetWatch server's IP) and UDP 161 on firewalls"
            : message;
    }

    private async Task<string> ServerAsync(string target, (CredentialKind Kind, CredentialSecret Secret) credential, List<ConnectionTestStep> steps, CancellationToken ct)
    {
        var s = _settings.Servers;
        var data = await _servers.CollectAsync(new ServerPollRequest(target, new ManagementCredential(credential.Kind, credential.Secret), [], new Dictionary<string, long>(),
            null, false, false, 1, s.WinRmHttps && s.WinRmPort == 5985 ? 5986 : s.WinRmPort, s.WinRmHttps, s.DcomFallback, s.SshPort, TimeSpan.FromSeconds(s.TimeoutSec),
            ReadGpus: true, RunNvidiaSmi: _settings.Hardware.RunNvidiaSmi), ct);
        steps.Add(new ConnectionTestStep(credential.Kind == CredentialKind.Ssh ? "SSH command" : "WMI query (Win32_OperatingSystem)", data.Ok,
            data.Ok ? $"{data.OsName} {data.OsVersion} via {data.Method}, {data.Volumes.Count} volume(s), CPU {data.CpuPct:0} %" : data.Error ?? "failed"));
        if (data.Ok && data.GpusRead)
        {
            steps.Add(new ConnectionTestStep("GPU query", data.GpuError is null,
                data.Gpus.Count == 0 ? "no GPU" : string.Join("; ", data.Gpus.Select(g => $"{g.Name} {g.UtilizationPct:0} % {g.TemperatureC:0} °C{(g.Problem is null ? string.Empty : $" ({g.Problem})")}")) + (data.GpuError is null ? string.Empty : $" — {data.GpuError}")));
        }

        return data.Ok ? $"Connected via {data.Method}" : "Failed";
    }

    private static async Task<string> EventLogAsync(string target, CredentialSecret secret, List<ConnectionTestStep> steps, CancellationToken ct)
    {
        var account = WindowsAccount.From(secret);
        var record = await Task.Run(() =>
        {
            using var session = new EventLogSession(target, account.Domain, account.User, account.SecurePassword(), SessionAuthentication.Negotiate);
            using var reader = new EventLogReader(new EventLogQuery("Security", PathType.LogName, "*") { Session = session, ReverseDirection = true });
            using var e = reader.ReadEvent();
            return e is null ? null : $"newest record {e.RecordId}: event {e.Id} at {e.TimeCreated?.ToUniversalTime():u}";
        }, ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        steps.Add(new ConnectionTestStep("Remote Security log read (1 event)", record is not null, record ?? "the Security log is empty"));
        return record is null ? "Empty log" : "Security log readable";
    }

    private async Task<string> VcenterAsync(string target, (CredentialKind Kind, CredentialSecret Secret) credential, List<ConnectionTestStep> steps, CancellationToken ct)
    {
        var v = _settings.Vmware;
        var data = await _vmware.CollectAsync(new VmwarePollRequest(target, new ManagementCredential(credential.Kind, credential.Secret), null, v.TrustOnFirstUse, false, false, false, 10, null,
            TimeSpan.FromSeconds(v.TimeoutSec)), ct);
        steps.Add(new ConnectionTestStep("vSphere login and inventory", data.Ok,
            data.Ok ? $"{data.FullName}: {data.Hosts.Count} host(s), {data.Vms.Count} VM(s), {data.Datastores.Count} datastore(s)" +
                      (data.NewThumbprint is { } tp ? $"; certificate not CA-trusted, would be pinned (SHA-256 {tp})" : string.Empty)
                    : data.Error ?? "failed"));
        return data.Ok ? "vCenter OK" : "Failed";
    }

    private async Task<string> RedfishAsync(string target, (CredentialKind Kind, CredentialSecret Secret) credential, List<ConnectionTestStep> steps, CancellationToken ct)
    {
        var h = _settings.Hardware;
        var data = await _hardware.CollectAsync(new HardwarePollRequest(target, new ManagementCredential(credential.Kind, credential.Secret), null, null, h.TrustOnFirstUse, TimeSpan.FromSeconds(h.TimeoutSec)), ct);
        steps.Add(new ConnectionTestStep("Redfish login and read", data.Ok,
            data.Ok
                ? $"{data.Components.Count} components ({data.Components.Count(c => c.IsProblem)} not OK){(data.PowerWatts is { } w ? $", {w:0} W" : string.Empty)}" +
                  (data.NewTlsThumbprint is { } tp ? $"; certificate not CA-trusted, would be pinned (SHA-256 {tp})" : string.Empty)
                : data.Error ?? "failed"));
        return data.Ok ? "iLO/iDRAC OK" : "Failed";
    }

    private async Task<string> LdapAsync(ConnectionTestRequest request, List<ConnectionTestStep> steps, CancellationToken ct)
    {
        var d = _settings.Ldap;
        if (!d.Enabled || d.Servers.Count == 0)
        {
            throw new ArgumentException("Directory sign-in is not configured (Settings → Directory).");
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            throw new ArgumentException("Enter a directory user name and password to test the bind.");
        }

        var r = await _ldap.AuthenticateAsync(d, request.Username.Trim(), request.Password, ct);
        steps.Add(new ConnectionTestStep($"LDAP bind ({d.Security}) to {string.Join(", ", d.Servers)}", r.Success,
            r.Success ? $"bound as {r.SamAccountName} ({r.DisplayName})" : r.Error ?? "failed"));
        if (r.Success)
        {
            steps.Add(new ConnectionTestStep("Group → role mapping", r.Role is not null,
                r.Role is { } role ? $"role {role} through {string.Join("; ", r.MatchedGroups)}" : "the account is in none of the mapped groups"));
        }

        return r.Success ? "Directory OK" : "Failed";
    }

    private async Task<string> SmtpAsync(List<ConnectionTestStep> steps, CancellationToken ct)
    {
        var error = await _notifications.TestAsync("smtp", ct);
        steps.Add(new ConnectionTestStep("SMTP test e-mail", error is null, error ?? $"sent through {_settings.Notifications.SmtpHost}"));
        return error is null ? "E-mail sent" : "Failed";
    }

    private static async Task<string> ReceiversAsync(EventsDbContext events, Core.Entities.Device? device, string target, int minutes, List<ConnectionTestStep> steps, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddMinutes(-minutes);
        var syslog = events.SyslogMessages.AsNoTracking().Where(m => m.ReceivedUtc >= since && (m.SourceIp == target || (device != null && m.DeviceId == device.Id)));
        var traps = events.SnmpTraps.AsNoTracking().Where(m => m.ReceivedUtc >= since && (m.SourceIp == target || (device != null && m.DeviceId == device.Id)));
        var s = await syslog.CountAsync(ct);
        var t = await traps.CountAsync(ct);
        var lastS = s > 0 ? await syslog.MaxAsync(m => m.ReceivedUtc, ct) : (DateTime?)null;
        var lastT = t > 0 ? await traps.MaxAsync(m => m.ReceivedUtc, ct) : (DateTime?)null;
        steps.Add(new ConnectionTestStep($"Syslog from {target} in the last {minutes} min", s > 0, s > 0 ? $"{s} message(s), last {lastS:u}" : "none — check the device's syslog target, UDP/TCP 514 and the firewall"));
        steps.Add(new ConnectionTestStep($"SNMP traps from {target} in the last {minutes} min", t > 0, t > 0 ? $"{t} trap(s), last {lastT:u}" : "none — check the trap destination/community, UDP 162 and the firewall"));
        return $"{s} syslog, {t} traps";
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(CredentialKind?, CredentialSecret?)> CredentialAsync(NetWatchDbContext db, int id, CancellationToken ct)
    {
        var p = await db.CredentialProfiles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new ArgumentException("The credential does not exist.");
        return (p.Kind, CredentialSecret.FromJson(_protector.Unprotect(p.SecretBlob)));
    }

    private static (CredentialKind Kind, CredentialSecret Secret) Need(CredentialKind? kind, CredentialSecret? secret, params CredentialKind[] allowed) =>
        kind is { } k && secret is not null && allowed.Contains(k)
            ? (k, secret)
            : throw new ArgumentException($"Choose a {string.Join(" or ", allowed)} credential for this test.");

    private static async Task<IPAddress> ResolveAsync(string target, List<ConnectionTestStep> steps, CancellationToken ct)
    {
        if (IPAddress.TryParse(target, out var ip))
        {
            return ip;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target, ct);
            var chosen = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.First();
            steps.Add(new ConnectionTestStep("DNS", true, $"{target} = {chosen}"));
            return chosen;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            steps.Add(new ConnectionTestStep("DNS", false, $"{target} cannot be resolved: {ex.Message}"));
            throw;
        }
    }

    internal static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied (check the account and its rights, e.g. Event Log Readers / Remote Management Users)",
        EventLogException e => $"{e.Message.Trim()} — RPC (TCP 135 + dynamic ports) or the Remote Event Log Management firewall rule may be blocked",
        TimeoutException => "timed out",
        System.Runtime.InteropServices.COMException com => $"{com.Message.Trim()} (0x{com.HResult:X8})",
        _ => ex.Message.Trim(),
    };
}
