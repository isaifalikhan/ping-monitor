using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Alerts.Engine;
using NetWatch.Alerts.Notifications;
using NetWatch.Collectors.DomainControllers;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Receivers;
using NetWatch.Collectors.Scheduling;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Backup;
using NetWatch.Data.Config;

namespace NetWatch.Web.Infrastructure;

/// <summary>Everything the self-checks look at, sampled once a minute.</summary>
public sealed record SelfCheckInput(
    DateTime NowUtc,
    DateTime StartedUtc,
    long DiskFreeBytes,
    long DiskTotalBytes,
    int Devices,
    int PollsQueued,
    int ConsecutiveBacklogMinutes,
    long MetricsDroppedDelta,
    int MetricsPending,
    long AlertSignalsDroppedDelta,
    int AlertSignalsQueued,
    long ReceiverDroppedDelta,
    DateTime? CertificateNotAfterUtc,
    bool BackupEnabled,
    DateTime? NewestBackupUtc,
    string? BackupError,
    string? ReportError,
    IReadOnlyList<ChannelStatus> Notifications,
    bool ForwardingEnabled,
    string? ForwardingError);

public sealed record SelfCheckResult(string Name, bool Ok, string Detail);

/// <summary>The self-check rules. Pure (unit tested).</summary>
public static class SelfChecks
{
    public const string Disk = "Data disk";
    public const string Polling = "Polling";
    public const string MetricStorage = "Metric storage";
    public const string AlertEngine = "Alert engine";
    public const string Receivers = "Syslog/trap intake";
    public const string Certificate = "Web certificate";
    public const string Backups = "Backups";
    public const string Reports = "Scheduled reports";
    public const string Notifications = "Notifications";
    public const string Forwarding = "Event forwarding";

    public static IReadOnlyList<SelfCheckResult> Evaluate(SelfCheckInput i, SelfMonitorSettings s)
    {
        var results = new List<SelfCheckResult>();
        void Add(string name, bool ok, string detail) => results.Add(new SelfCheckResult(name, ok, detail));
        string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";

        if (i.DiskTotalBytes > 0)
        {
            var pct = 100.0 * i.DiskFreeBytes / i.DiskTotalBytes;
            var ok = pct >= s.MinFreeDiskPct && i.DiskFreeBytes >= s.MinFreeDiskGb * 1073741824L;
            Add(Disk, ok, string.Create(CultureInfo.InvariantCulture, $"{Gb(i.DiskFreeBytes)} free of {Gb(i.DiskTotalBytes)} ({pct:0.#} %)") +
                (ok ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"; minimum {s.MinFreeDiskPct} % and {s.MinFreeDiskGb} GB")));
        }

        // A short queue is normal right after start or a burst; a backlog that persists means too few workers.
        var backlog = i.PollsQueued > Math.Max(20, i.Devices / 4) && i.ConsecutiveBacklogMinutes >= 3;
        Add(Polling, !backlog, backlog
            ? string.Create(CultureInfo.InvariantCulture, $"{i.PollsQueued} polls waiting for {i.ConsecutiveBacklogMinutes} min: polling cannot keep up (raise 'Parallel polls' or the polling interval)")
            : string.Create(CultureInfo.InvariantCulture, $"{i.PollsQueued} polls waiting"));

        var metricsOk = i.MetricsDroppedDelta == 0 && i.MetricsPending < 200_000;
        Add(MetricStorage, metricsOk, metricsOk
            ? string.Create(CultureInfo.InvariantCulture, $"{i.MetricsPending} samples pending")
            : string.Create(CultureInfo.InvariantCulture, $"{i.MetricsDroppedDelta} samples dropped in the last minute, {i.MetricsPending} pending (disk too slow or full)"));

        var alertsOk = i.AlertSignalsDroppedDelta == 0 && i.AlertSignalsQueued < 50_000;
        Add(AlertEngine, alertsOk, alertsOk
            ? string.Create(CultureInfo.InvariantCulture, $"{i.AlertSignalsQueued} signals queued")
            : string.Create(CultureInfo.InvariantCulture, $"{i.AlertSignalsDroppedDelta} signals dropped in the last minute, {i.AlertSignalsQueued} queued"));

        Add(Receivers, i.ReceiverDroppedDelta == 0, i.ReceiverDroppedDelta == 0
            ? "No messages dropped"
            : string.Create(CultureInfo.InvariantCulture, $"{i.ReceiverDroppedDelta} syslog/trap messages dropped in the last minute (rate limit reached: a message storm?)"));

        if (i.CertificateNotAfterUtc is { } notAfter)
        {
            var days = (notAfter - i.NowUtc).TotalDays;
            Add(Certificate, days >= s.CertificateWarnDays, days < 0
                ? $"Expired on {LocalTime.ToLocal(notAfter):yyyy-MM-dd}"
                : string.Create(CultureInfo.InvariantCulture, $"Valid until {LocalTime.ToLocal(notAfter):yyyy-MM-dd} ({days:0} days)"));
        }

        if (i.BackupEnabled)
        {
            var maxAge = TimeSpan.FromHours(s.MaxBackupAgeHours);
            var tooOld = i.NewestBackupUtc is { } newest ? i.NowUtc - newest > maxAge : i.NowUtc - i.StartedUtc > maxAge;
            var ok = i.BackupError is null && !tooOld;
            Add(Backups, ok,
                i.BackupError is not null ? $"Last backup failed: {i.BackupError}"
                : i.NewestBackupUtc is { } n ? string.Create(CultureInfo.InvariantCulture, $"Newest backup {LocalTime.ToLocal(n):yyyy-MM-dd HH:mm} PKT{(tooOld ? $" (older than {s.MaxBackupAgeHours} h)" : string.Empty)}")
                : tooOld ? "No backup has been made" : "No backup yet (first one at the scheduled time)");
        }

        Add(Reports, i.ReportError is null, i.ReportError is null ? "All scheduled reports succeeded" : $"Last scheduled report failed: {i.ReportError}");

        var failing = i.Notifications.Where(c => c.LastErrorUtc is { } e && (c.LastSuccessUtc is null || e > c.LastSuccessUtc) && i.NowUtc - e < TimeSpan.FromHours(1)).ToList();
        Add(Notifications, failing.Count == 0, failing.Count == 0
            ? "No delivery errors in the last hour"
            : string.Join("; ", failing.Select(c => $"{c.Channel}: {c.LastError}")));

        if (i.ForwardingEnabled)
        {
            Add(Forwarding, i.ForwardingError is null, i.ForwardingError ?? "Reading the ForwardedEvents log");
        }

        return results;
    }
}

/// <summary>
/// NetWatch monitors itself: once a minute the self-checks run and each one is published as a state of the
/// "NetWatch server" device (created at start, 127.0.0.1), so problems raise normal alerts and notifications.
/// </summary>
public sealed class SelfMonitorService : BackgroundService
{
    public const string SelfTag = "netwatch-self";

    private readonly IServiceProvider _services;
    private readonly ILogger<SelfMonitorService> _logger;
    private readonly Dictionary<string, long> _previous = new();
    private int _backlogMinutes;

    public SelfMonitorService(IServiceProvider services, ILogger<SelfMonitorService> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>Latest results (system status page).</summary>
    public IReadOnlyList<SelfCheckResult> Latest { get; private set; } = [];
    public DateTime? LastRunUtc { get; private set; }
    public int? SelfDeviceId { get; private set; }

    /// <summary>Report scheduler failures (set by the scheduler).</summary>
    public string? ReportError { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            SelfDeviceId = await EnsureSelfDeviceAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create the NetWatch self-monitoring device");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                if (SelfDeviceId is null)
                {
                    // Retry after a failed start (e.g. the database was busy).
                    SelfDeviceId = await EnsureSelfDeviceAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Self-monitoring device still unavailable");
            }

            try
            {
                RunOnce();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Self-check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public void RunOnce()
    {
        var paths = _services.GetRequiredService<NetWatchPaths>();
        var stats = _services.GetRequiredService<CollectorStats>();
        var metrics = _services.GetRequiredService<IMetricStore>();
        var alerts = _services.GetRequiredService<AlertManager>();
        var receivers = _services.GetRequiredService<ReceiverStats>();
        var inventory = _services.GetRequiredService<InventoryCache>();
        var backups = _services.GetRequiredService<BackupService>();
        var settings = _services.GetRequiredService<SettingsService>();
        var notifications = _services.GetRequiredService<NotificationDispatcher>();
        var forwarding = _services.GetRequiredService<ForwardedEventsReader>();
        var certificate = _services.GetService<ServerCertificateInfo>();
        var now = DateTime.UtcNow;

        long Delta(string key, long value)
        {
            var delta = _previous.TryGetValue(key, out var last) ? Math.Max(0, value - last) : 0;
            _previous[key] = value;
            return delta;
        }

        var devices = inventory.All.Count;
        _backlogMinutes = stats.Queued > Math.Max(20, devices / 4) ? _backlogMinutes + 1 : 0;
        var drive = new DriveInfo(Path.GetPathRoot(paths.Root) ?? "C:\\");
        var input = new SelfCheckInput(
            now,
            stats.StartedUtc,
            drive.IsReady ? drive.AvailableFreeSpace : 0,
            drive.IsReady ? drive.TotalSize : 0,
            devices,
            stats.Queued,
            _backlogMinutes,
            Delta("metrics", metrics.DroppedSamples),
            metrics.PendingWrites,
            Delta("alerts", alerts.DroppedSignals),
            alerts.QueuedSignals,
            Delta("receivers", receivers.Dropped),
            certificate?.NotAfterUtc,
            settings.Backup.Enabled,
            backups.NewestUtc(),
            backups.LastError,
            ReportError,
            notifications.Status,
            settings.Dc.AnyForwarding,
            forwarding.LastError);

        Latest = SelfChecks.Evaluate(input, settings.SelfMonitor);
        LastRunUtc = now;
        if (SelfDeviceId is not { } id)
        {
            return;
        }

        var sink = _services.GetRequiredService<IAlertSignalSink>();
        foreach (var r in Latest)
        {
            sink.Publish(new StateSignal(id, null, StateKeys.SelfCheck, r.Name, !r.Ok, $"{r.Name}: {r.Detail}", now, r.Name));
        }
    }

    private async Task<int> EnsureSelfDeviceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var existing = await db.Devices.Where(d => d.Tags.Any(t => t.Tag == SelfTag)).Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id)
        {
            return id;
        }

        // This server may already be a device at 127.0.0.1 (addresses are unique): adopt it instead.
        var local = await db.Devices.Include(d => d.Tags).FirstOrDefaultAsync(d => d.IpAddress == "127.0.0.1", ct);
        if (local is not null)
        {
            local.Tags.Add(new DeviceTag { Tag = SelfTag });
            await db.SaveChangesAsync(ct);
            _services.GetRequiredService<IInventoryChangeNotifier>().NotifyInventoryChanged();
            _logger.LogInformation("Using existing device {Name} (127.0.0.1) for self-monitoring", local.Name);
            return local.Id;
        }

        var now = DateTime.UtcNow;
        var device = new Device
        {
            Name = $"{Environment.MachineName} (NetWatch)",
            IpAddress = "127.0.0.1",
            Type = DeviceType.Other,
            Os = OsKind.Windows,
            PollIntervalSec = 60,
            Status = DeviceStatus.Unknown,
            StatusSinceUtc = now,
            CreatedUtc = now,
            UpdatedUtc = now,
            Notes = "Created automatically: NetWatch monitors itself (disk, polling, storage, backups, certificate). Do not delete.",
            Tags = [new DeviceTag { Tag = SelfTag }],
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        _services.GetRequiredService<IInventoryChangeNotifier>().NotifyInventoryChanged();
        _logger.LogInformation("Created self-monitoring device {Name} (id {Id})", device.Name, device.Id);
        return device.Id;
    }
}
