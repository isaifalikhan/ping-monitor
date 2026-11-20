using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Core;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Data.Metrics;

namespace NetWatch.Data;

/// <summary>
/// Nightly purge (02:30 local time) of expired metric shards, events, audit entries, closed alerts and
/// expired sessions, according to <see cref="Core.Configuration.RetentionSettings"/>.
/// </summary>
public sealed class RetentionService : BackgroundService
{
    private static readonly CronExpression Schedule = CronExpression.Parse("30 2 * * *");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SqliteMetricStore _metrics;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(IServiceScopeFactory scopeFactory, SqliteMetricStore metrics, SettingsService settings, TimeProvider time, ILogger<RetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Also run shortly after start so an instance that was off at 02:30 still purges.
        await SafeDelay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            var now = _time.GetUtcNow();
            var next = Schedule.GetNextOccurrence(now, TimeZoneInfo.Local) ?? now.AddDays(1);
            await SafeDelay(next - now, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var retention = _settings.Retention;
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            var dropped = _metrics.ApplyRetention(retention, now);
            if (dropped.Count > 0)
            {
                _logger.LogInformation("Retention dropped {Count} metric shard(s): {Tables}", dropped.Count, string.Join(", ", dropped));
            }

            using var scope = _scopeFactory.CreateScope();
            var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
            var eventCutoff = now.AddDays(-retention.EventDays);
            var purgedEvents = await events.DeviceEvents.Where(e => e.TimestampUtc < eventCutoff).ExecuteDeleteAsync(ct);

            // High-volume tables are purged in batches so the event writer is never blocked for long.
            purgedEvents += await PurgeInBatchesAsync(() => events.SyslogMessages.Where(e => e.ReceivedUtc < eventCutoff).OrderBy(e => e.Id).Take(50_000).ExecuteDeleteAsync(ct));
            purgedEvents += await PurgeInBatchesAsync(() => events.SnmpTraps.Where(e => e.ReceivedUtc < eventCutoff).OrderBy(e => e.Id).Take(50_000).ExecuteDeleteAsync(ct));
            purgedEvents += await PurgeInBatchesAsync(() => events.WindowsEvents.Where(e => e.TimestampUtc < eventCutoff).OrderBy(e => e.Id).Take(50_000).ExecuteDeleteAsync(ct));

            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var auditCutoff = now.AddDays(-retention.AuditDays);
            var purgedAudit = await db.AuditLog.Where(a => a.TimestampUtc < auditCutoff).ExecuteDeleteAsync(ct);

            var alertCutoff = now.AddDays(-retention.ClosedAlertDays);
            var purgedAlerts = await db.Alerts
                .Where(a => (a.State == AlertState.Closed || a.State == AlertState.Resolved) && a.LastSeenUtc < alertCutoff)
                .ExecuteDeleteAsync(ct);

            var purgedSessions = await db.UserSessions
                .Where(s => s.ExpiresUtc < now || s.RevokedUtc != null)
                .ExecuteDeleteAsync(ct);

            _logger.LogInformation(
                "Retention purged {Events} events, {Audit} audit entries, {Alerts} old alerts, {Sessions} sessions",
                purgedEvents, purgedAudit, purgedAlerts, purgedSessions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Retention run failed");
        }
    }

    private static async Task<int> PurgeInBatchesAsync(Func<Task<int>> deleteBatch)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await deleteBatch();
            total += deleted;
            if (deleted > 0)
            {
                // Let the event writer get a turn between batches.
                await Task.Delay(50);
            }
        }
        while (deleted >= 50_000);

        return total;
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
