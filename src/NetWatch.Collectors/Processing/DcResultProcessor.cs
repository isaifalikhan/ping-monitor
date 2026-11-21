using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.DomainControllers;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Polling;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Core.Credentials;
using NetWatch.Core.Entities;
using NetWatch.Core.Metrics;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Config;
using NetWatch.Data.Events;

namespace NetWatch.Collectors.Processing;

/// <summary>
/// Domain controller part of poll processing: builds the DC request (bookmarks, replication interval), filters
/// and stores Security-log events, publishes them to the alert engine with their fields (target account, source,
/// group, off-hours ...), stores Directory Service / DFSR problems, and keeps DC facts, replication partners
/// and the clock skew to the PDC emulator.
/// </summary>
public sealed class DcResultProcessor
{
    private static readonly TimeSpan LinkExpiry = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMetricStore _metrics;
    private readonly IAlertSignalSink _alerts;
    private readonly SettingsService _settings;
    private readonly CredentialResolver _credentials;
    private readonly TimeProvider _time;
    private readonly ILogger<DcResultProcessor> _logger;
    private readonly ConcurrentDictionary<int, DcRuntime> _runtime = new();

    public DcResultProcessor(
        IServiceScopeFactory scopeFactory,
        IMetricStore metrics,
        IAlertSignalSink alerts,
        SettingsService settings,
        CredentialResolver credentials,
        TimeProvider time,
        ILogger<DcResultProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _alerts = alerts;
        _settings = settings;
        _credentials = credentials;
        _time = time;
        _logger = logger;
    }

    private sealed class DcRuntime
    {
        public bool Loaded;
        public Dictionary<string, long> Bookmarks = new(StringComparer.OrdinalIgnoreCase);
        public DateTime LastReplicationCheckUtc;
    }

    /// <summary>Security events stored since start (self-monitoring).</summary>
    public long StoredEvents => Interlocked.Read(ref _stored);
    private long _stored;

    public void Forget(int deviceId) => _runtime.TryRemove(deviceId, out _);

    public async Task<DcPollRequest?> PrepareAsync(DeviceSnapshot device, CancellationToken ct)
    {
        if (device.Type != DeviceType.DomainController || device.ManagementCredentialId is not { } credentialId)
        {
            return null;
        }

        var credential = await _credentials.GetManagementAsync(credentialId, ct);
        if (credential is not { Kind: CredentialKind.Windows })
        {
            return null;
        }

        var rt = await GetRuntimeAsync(device.Id, ct);
        var dc = _settings.Dc;
        var servers = _settings.Servers;
        var now = _time.GetUtcNow().UtcDateTime;
        var checkReplication = now - rt.LastReplicationCheckUtc >= TimeSpan.FromMinutes(dc.ReplicationCheckMin);
        if (checkReplication)
        {
            rt.LastReplicationCheckUtc = now;
        }

        return new DcPollRequest(
            device.IpAddress,
            credential,
            new Dictionary<string, long>(rt.Bookmarks, StringComparer.OrdinalIgnoreCase),
            dc.ReadSecurityLog && !dc.UseForwardedEvents,
            dc.MaxSecurityEventsPerPoll,
            checkReplication,
            servers.WinRmHttps && servers.WinRmPort == 5985 ? 5986 : servers.WinRmPort,
            servers.WinRmHttps,
            servers.DcomFallback,
            TimeSpan.FromSeconds(servers.TimeoutSec));
    }

    public async Task ProcessAsync(DeviceSnapshot device, DevicePollResult result, NetWatchDbContext db, EventsDbContext events, CancellationToken ct)
    {
        if (result.Dc is not { } dc)
        {
            return;
        }

        var now = result.TimestampUtc;
        var rt = await GetRuntimeAsync(device.Id, ct);

        await IngestSecurityAsync(device, dc.SecurityEvents, events, ct);
        await DirectoryEventsAsync(device, rt, dc, events, now, ct);
        await InfoAsync(device, dc, db, now, ct);
        if (dc.ReplicationChecked && dc.Replication is { } links)
        {
            await ReplicationAsync(device, links, db, now, ct);
        }

        if (dc.SkewToPdcSec is { } skew)
        {
            var abs = Math.Abs(skew);
            _alerts.Publish(new MetricSignal(device.Id, null, MetricKeys.DcTimeSkew, null, abs, now));
            var id = _metrics.GetOrCreateSeries(device.Id, MetricKeys.DcTimeSkew, null);
            _metrics.Enqueue(new GenericSample(id, new DateTimeOffset(now).ToUnixTimeMilliseconds(), abs));
        }
    }

    // ------------------------------------------------------------------ security events

    /// <summary>
    /// Filters, stores and publishes Security-log events of one DC (remote reads and Windows Event Forwarding).
    /// Duplicates (same DC and record ID) are skipped. Returns the number stored.
    /// </summary>
    public async Task<int> IngestSecurityAsync(DeviceSnapshot device, IReadOnlyList<DcSecurityReading> readings, EventsDbContext events, CancellationToken ct)
    {
        if (readings.Count == 0)
        {
            return 0;
        }

        var s = _settings.Dc;
        var kept = readings.Where(r => Keep(r, s)).GroupBy(r => r.RecordId).Select(g => g.First()).ToList();
        if (kept.Count == 0)
        {
            return 0;
        }

        var ids = kept.Select(r => r.RecordId).ToList();
        var existing = (await events.DcSecurityEvents.AsNoTracking()
            .Where(e => e.DeviceId == device.Id && ids.Contains(e.RecordId))
            .Select(e => e.RecordId)
            .ToListAsync(ct)).ToHashSet();

        var signals = new List<EventSignal>();
        foreach (var r in kept.Where(r => !existing.Contains(r.RecordId)).OrderBy(r => r.TimestampUtc))
        {
            var privileged = IsGroupChange(r.EventId) && s.IsPrivilegedGroup(r.GroupName);
            var severity = DcEventCatalog.SeverityOf(r.EventId, r.Failed, privileged);
            var category = DcEventCatalog.Events.TryGetValue(r.EventId, out var e) ? e.Category : DcEventCategory.Directory;
            var message = DcEventParser.Describe(r);
            events.DcSecurityEvents.Add(new DcSecurityEvent
            {
                DeviceId = device.Id,
                TimestampUtc = r.TimestampUtc,
                EventId = r.EventId,
                RecordId = r.RecordId,
                Severity = severity,
                Category = category.ToString(),
                SubjectUser = Trim(r.SubjectUser, 256),
                SubjectDomain = Trim(r.SubjectDomain, 256),
                TargetUser = Trim(r.TargetUser, 256),
                TargetDomain = Trim(r.TargetDomain, 256),
                GroupName = Trim(r.GroupName, 256),
                ObjectName = Trim(r.ObjectName, 512),
                SourceIp = Trim(r.SourceIp, 64),
                Workstation = Trim(r.Workstation, 256),
                LogonType = r.LogonType,
                Status = Trim(r.Status, 32),
                SubStatus = Trim(r.SubStatus, 32),
                Message = message,
            });

            // Successful logons and ticket requests are stored for investigation but not evaluated by rules (volume).
            if (IsSuccessfulAuthentication(r))
            {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Add(fields, "targetUser", r.TargetUser);
            Add(fields, "subjectUser", r.SubjectUser);
            Add(fields, "sourceIp", r.SourceIp);
            Add(fields, "workstation", r.Workstation);
            Add(fields, "source", r.SourceIp ?? r.Workstation);
            Add(fields, "group", r.GroupName);
            Add(fields, "logonType", r.LogonType?.ToString(CultureInfo.InvariantCulture));
            fields["failed"] = r.Failed ? "true" : "false";
            fields["privileged"] = privileged ? "true" : "false";
            fields["offHours"] = s.IsWorkingTime(r.TimestampUtc) ? "false" : "true";
            signals.Add(new EventSignal(device.Id, EventKeys.DcSecurity, message, r.TimestampUtc, severity, $"Security {r.EventId}", r.EventId, null, fields));
        }

        var added = events.ChangeTracker.Entries<DcSecurityEvent>().Count(x => x.State == EntityState.Added);
        try
        {
            await events.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Could not store Security events of {Device}", device.Name);
            events.ChangeTracker.Clear();
            return 0;
        }

        Interlocked.Add(ref _stored, added);
        foreach (var signal in signals)
        {
            _alerts.Publish(signal);
        }

        return added;
    }

    /// <summary>Applies the storage filters of <see cref="Core.Configuration.DcSettings"/>.</summary>
    internal static bool Keep(DcSecurityReading r, Core.Configuration.DcSettings s)
    {
        switch (r.EventId)
        {
            case 4624:
                return s.StoreSuccessfulLogons &&
                       (s.SuccessfulLogonTypes.Count == 0 || (r.LogonType is { } t && s.SuccessfulLogonTypes.Contains(t))) &&
                       !(s.ExcludeComputerAccounts && DcEventParser.IsComputerAccount(r.TargetUser)) &&
                       !DcEventParser.IsServiceIdentity(r.TargetUser);
            case 4768 or 4776 when !r.Failed:
                return s.StoreSuccessfulLogons && !(s.ExcludeComputerAccounts && DcEventParser.IsComputerAccount(r.TargetUser));
            case 4625 or 4768 or 4771 or 4776:
                return !(s.ExcludeComputerAccounts && DcEventParser.IsComputerAccount(r.TargetUser));
            case 4672:
                // Machine accounts and service identities get special privileges constantly: only people matter here.
                return !DcEventParser.IsComputerAccount(r.SubjectUser) && !DcEventParser.IsServiceIdentity(r.SubjectUser);
            default:
                return true;
        }
    }

    private static bool IsSuccessfulAuthentication(DcSecurityReading r) => r.EventId == 4624 || (r.EventId is 4768 or 4776 && !r.Failed);

    private static bool IsGroupChange(int eventId) => eventId is 4728 or 4729 or 4732 or 4733 or 4756 or 4757;

    private static void Add(Dictionary<string, string> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[name] = value;
        }
    }

    // ------------------------------------------------------------------ directory service / DFSR

    private async Task DirectoryEventsAsync(DeviceSnapshot device, DcRuntime rt, DcPollData dc, EventsDbContext events, DateTime now, CancellationToken ct)
    {
        foreach (var e in dc.DirectoryEvents.OrderBy(e => e.TimestampUtc))
        {
            if (rt.Bookmarks.TryGetValue(e.LogName, out var last) && e.RecordId <= last)
            {
                continue;
            }

            events.WindowsEvents.Add(new WindowsEventEntry
            {
                DeviceId = device.Id,
                LogName = e.LogName,
                RecordId = e.RecordId,
                TimestampUtc = e.TimestampUtc,
                Level = e.Level,
                EventId = e.EventId,
                Provider = Trim(e.Provider, 200),
                Message = e.Message,
            });

            // Replication problems are raised as Major whatever level the log gave them.
            var severity = DcEventCatalog.ReplicationEventIds.Contains(e.EventId) ? Severity.Major : SeverityMapping.FromWindowsLevel(e.Level);
            _alerts.Publish(new EventSignal(device.Id, EventKeys.WindowsEvent, $"{e.LogName} event {e.EventId} from {e.Provider}: {e.Message}", e.TimestampUtc,
                severity, e.Provider, e.EventId));
        }

        foreach (var (log, recordId) in dc.NewBookmarks)
        {
            var existing = await events.EventBookmarks.FirstOrDefaultAsync(b => b.DeviceId == device.Id && b.LogName == log, ct);
            if (existing is null)
            {
                events.EventBookmarks.Add(new EventBookmark { DeviceId = device.Id, LogName = log, LastRecordId = recordId, UpdatedUtc = now });
            }
            else
            {
                existing.LastRecordId = recordId;
                existing.UpdatedUtc = now;
            }

            rt.Bookmarks[log] = recordId;
        }

        try
        {
            await events.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Could not store directory events of {Device}", device.Name);
            events.ChangeTracker.Clear();
        }
    }

    // ------------------------------------------------------------------ facts and replication

    private static async Task InfoAsync(DeviceSnapshot device, DcPollData dc, NetWatchDbContext db, DateTime now, CancellationToken ct)
    {
        var info = await db.DcInfos.FirstOrDefaultAsync(i => i.DeviceId == device.Id, ct);
        if (info is null)
        {
            info = new DcInfo { DeviceId = device.Id };
            db.DcInfos.Add(info);
        }

        info.LastSeenUtc = now;
        info.SecurityLogError = Trim(dc.SecurityLogError);
        if (dc.ReplicationChecked)
        {
            info.LastReplicationCheckUtc = now;
            info.ReplicationError = Trim(dc.ReplicationError ?? dc.TimeError);
            if (dc.Topology is { } t)
            {
                info.DcName = Trim(t.DcName, 256) ?? info.DcName;
                info.Domain = Trim(t.Domain, 256) ?? info.Domain;
                info.Forest = Trim(t.Forest, 256) ?? info.Forest;
                info.Site = Trim(t.Site, 256) ?? info.Site;
                info.IsGlobalCatalog = t.IsGlobalCatalog;
                info.Roles = t.Roles.Count == 0 ? null : string.Join(", ", t.Roles);
                info.PdcEmulator = Trim(t.PdcEmulator, 256) ?? info.PdcEmulator;
            }

            info.ClockOffsetSec = dc.ClockOffsetSec ?? info.ClockOffsetSec;
            info.SkewToPdcSec = dc.SkewToPdcSec ?? info.SkewToPdcSec;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ReplicationAsync(DeviceSnapshot device, List<DcReplicationReading> readings, NetWatchDbContext db, DateTime now, CancellationToken ct)
    {
        var rows = await db.DcReplicationLinks.Where(l => l.DeviceId == device.Id).ToListAsync(ct);
        foreach (var r in readings)
        {
            var nc = DcReplicationParser.NamingContextLabel(r.NamingContext);
            var partner = Trim(r.Partner, 256)!;
            var row = rows.FirstOrDefault(x => string.Equals(x.Partner, partner, StringComparison.OrdinalIgnoreCase) && string.Equals(x.NamingContext, nc, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new DcReplicationLink { DeviceId = device.Id, Partner = partner, NamingContext = Trim(nc, 512)! };
                db.DcReplicationLinks.Add(row);
                rows.Add(row);
            }

            row.LastSuccessUtc = r.LastSuccessUtc;
            row.LastAttemptUtc = r.LastAttemptUtc;
            row.LastResult = r.LastResult;
            row.ConsecutiveFailures = r.ConsecutiveFailures;
            row.LastMessage = r.LastResult == 0 ? null : Win32Text(r.LastResult);
            row.LastSeenUtc = now;

            var failing = r.LastResult != 0 && r.ConsecutiveFailures > 0;
            var instance = $"{partner} / {nc}";
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.DcReplicationFailed, instance, failing,
                failing
                    ? string.Create(CultureInfo.InvariantCulture, $"Inbound replication of {nc} from {partner} failing: {row.LastMessage} ({r.ConsecutiveFailures} consecutive failures, last success {(r.LastSuccessUtc is { } ls ? LocalTime.ToLocal(ls).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " PKT" : "never")})")
                    : $"Inbound replication of {nc} from {partner} is working",
                now, instance));
        }

        // Partners removed from the topology disappear after a day.
        var stale = rows.Where(r => now - r.LastSeenUtc > LinkExpiry).ToList();
        foreach (var r in stale)
        {
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.DcReplicationFailed, $"{r.Partner} / {r.NamingContext}", false, "Replication partner removed", now));
        }

        db.DcReplicationLinks.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
    }

    internal static string Win32Text(int code)
    {
        var text = new Win32Exception(code).Message;
        return string.Create(CultureInfo.InvariantCulture, $"{text} ({code})");
    }

    private async Task<DcRuntime> GetRuntimeAsync(int deviceId, CancellationToken ct)
    {
        var rt = _runtime.GetOrAdd(deviceId, _ => new DcRuntime());
        if (rt.Loaded)
        {
            return rt;
        }

        using var scope = _scopeFactory.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var logs = DcLogs.All.ToList();
        rt.Bookmarks = await events.EventBookmarks.AsNoTracking().Where(b => b.DeviceId == deviceId && logs.Contains(b.LogName))
            .ToDictionaryAsync(b => b.LogName, b => b.LastRecordId, StringComparer.OrdinalIgnoreCase, ct);
        rt.LastReplicationCheckUtc = await db.DcInfos.AsNoTracking().Where(i => i.DeviceId == deviceId)
            .Select(i => i.LastReplicationCheckUtc).FirstOrDefaultAsync(ct) ?? DateTime.MinValue;
        rt.Loaded = true;
        return rt;
    }

    private static string? Trim(string? s, int max = 1000) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s.Trim() : s[..max];
}
