using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Alerts.Notifications;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Reports;

public sealed record ReportFile(string Path, string Name, long SizeBytes, DateTime CreatedUtc);

/// <summary>Generates reports (on demand and scheduled), stores scheduled ones and e-mails them.</summary>
public sealed partial class ReportService
{
    /// <summary>Generated report files are deleted after this many days.</summary>
    public const int FileRetentionDays = 400;

    private const long MaxAttachmentBytes = 15 * 1024 * 1024;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NetWatchPaths _paths;
    private readonly MailSender _mail;
    private readonly ILogger<ReportService> _logger;

    // Reports read a lot of history: at most two are built at the same time.
    private readonly SemaphoreSlim _concurrency = new(2, 2);

    public ReportService(IServiceScopeFactory scopeFactory, NetWatchPaths paths, MailSender mail, ILogger<ReportService> logger)
    {
        _scopeFactory = scopeFactory;
        _paths = paths;
        _mail = mail;
        _logger = logger;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}/[a-z0-9_\-]{1,120}\.(pdf|csv)$", RegexOptions.CultureInvariant)]
    private static partial Regex FilePattern();

    /// <summary>Builds the format-neutral document (the on-screen preview uses it directly).</summary>
    public async Task<ReportDocument> BuildAsync(ReportRequest request, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var builder = new ReportBuilder(
                scope.ServiceProvider.GetRequiredService<NetWatchDbContext>(),
                scope.ServiceProvider.GetRequiredService<EventsDbContext>(),
                scope.ServiceProvider.GetRequiredService<IMetricStore>(),
                scope.ServiceProvider.GetRequiredService<SettingsService>());
            return await builder.BuildAsync(request, ct);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public async Task<(byte[] Content, string FileName, string MimeType)> GenerateAsync(ReportRequest request, ReportFormat format, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var builder = new ReportBuilder(
                scope.ServiceProvider.GetRequiredService<NetWatchDbContext>(),
                scope.ServiceProvider.GetRequiredService<EventsDbContext>(),
                scope.ServiceProvider.GetRequiredService<IMetricStore>(),
                scope.ServiceProvider.GetRequiredService<SettingsService>());
            var doc = await builder.BuildAsync(request, ct);
            var content = format == ReportFormat.Pdf ? PdfReportRenderer.Render(doc) : CsvReportRenderer.Render(doc);
            var stem = $"netwatch-{Slug(ReportCatalog.NameOf(request.Type))}-{LocalTime.ToLocal(request.FromUtc):yyyyMMdd}-{LocalTime.ToLocal(request.ToUtc.AddSeconds(-1)):yyyyMMdd}";
            return format == ReportFormat.Pdf ? (content, stem + ".pdf", "application/pdf") : (content, stem + ".csv", "text/csv");
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// The period a scheduled run covers: the previous local day, the previous 7 days, or the previous calendar
    /// month (all ending at local midnight before <paramref name="runUtc"/>).
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtc) PeriodFor(ReportFrequency frequency, DateTime runUtc)
    {
        var today = LocalTime.ToLocal(runUtc).Date;
        var (from, to) = frequency switch
        {
            ReportFrequency.Daily => (today.AddDays(-1), today),
            ReportFrequency.Weekly => (today.AddDays(-7), today),
            _ => (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1)),
        };
        return (LocalTime.ToUtc(from), LocalTime.ToUtc(to));
    }

    /// <summary>First scheduled run (UTC) strictly after <paramref name="afterUtc"/>.</summary>
    public static DateTime NextRunUtc(ReportSchedule s, DateTime afterUtc)
    {
        var time = TimeSpan.TryParseExact(s.TimeOfDay, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(7, 0, 0);
        var after = LocalTime.ToLocal(afterUtc);
        for (var day = after.Date; day < after.Date.AddDays(70); day = day.AddDays(1))
        {
            var candidate = day + time;
            var matches = s.Frequency switch
            {
                ReportFrequency.Daily => true,
                ReportFrequency.Weekly => (int)day.DayOfWeek == s.DayOfWeek,
                _ => day.Day == Math.Clamp(s.DayOfMonth, 1, 28),
            };
            if (matches && candidate > after)
            {
                return LocalTime.ToUtc(candidate);
            }
        }

        return afterUtc.AddDays(1);
    }

    /// <summary>Runs one schedule now: generates, saves and (if configured) e-mails the report. Returns the status text.</summary>
    public async Task<string> RunScheduleAsync(int scheduleId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var s = await db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == scheduleId, ct) ?? throw new KeyNotFoundException("Schedule not found.");
        var now = DateTime.UtcNow;
        var (from, to) = PeriodFor(s.Frequency, now);
        string status;
        try
        {
            var (content, _, mime) = await GenerateAsync(new ReportRequest(s.ReportType, from, to, s.GroupId), s.Format, ct);
            var relative = $"{LocalTime.ToLocal(now):yyyy-MM}/{Slug(s.Name)}_{LocalTime.ToLocal(from):yyyyMMdd}-{LocalTime.ToLocal(to.AddSeconds(-1)):yyyyMMdd}.{(s.Format == ReportFormat.Pdf ? "pdf" : "csv")}";
            var path = Path.Combine(_paths.ReportsDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content, ct);
            s.LastFile = relative;
            status = $"OK: saved {relative}";

            if (!string.IsNullOrWhiteSpace(s.EmailTo))
            {
                if (!_mail.IsConfigured)
                {
                    status += "; e-mail not sent: no SMTP server configured (Settings → Notifications)";
                }
                else
                {
                    var subject = $"HIT SIEM report: {s.Name} ({LocalTime.ToLocal(from):yyyy-MM-dd} – {LocalTime.ToLocal(to.AddSeconds(-1)):yyyy-MM-dd})";
                    var body = $"Scheduled report \"{s.Name}\" ({ReportCatalog.NameOf(s.ReportType)}) for {LocalTime.ToLocal(from):yyyy-MM-dd HH:mm} – {LocalTime.ToLocal(to):yyyy-MM-dd HH:mm} PKT.\r\n\r\n"
                               + (content.Length <= MaxAttachmentBytes ? "The report is attached." : $"The report is too large to attach; it is saved on the HIT SIEM server as {relative}.")
                               + "\r\n\r\n-- HIT SIEM";
                    var attachments = content.Length <= MaxAttachmentBytes ? new List<(string, byte[], string)> { (Path.GetFileName(path), content, mime) } : [];
                    await _mail.SendAsync(s.EmailTo, subject, body, attachments, ct);
                    status += $"; e-mailed to {s.EmailTo}";
                }
            }

            _logger.LogInformation("Scheduled report {Name}: {Status}", s.Name, status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = $"Failed: {ex.Message}";
            _logger.LogError(ex, "Scheduled report {Name} failed", s.Name);
        }

        s.LastRunUtc = now;
        s.LastStatus = status.Length > 1000 ? status[..1000] : status;
        await db.SaveChangesAsync(ct);
        return status;
    }

    public IReadOnlyList<ReportFile> ListFiles()
    {
        if (!Directory.Exists(_paths.ReportsDir))
        {
            return [];
        }

        return Directory.EnumerateFiles(_paths.ReportsDir, "*.*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(_paths.ReportsDir, f).Replace('\\', '/')))
            .Where(x => FilePattern().IsMatch(x.Rel))
            .Select(x => new ReportFile(x.Rel, Path.GetFileName(x.Full), new FileInfo(x.Full).Length, File.GetLastWriteTimeUtc(x.Full)))
            .OrderByDescending(f => f.CreatedUtc)
            .Take(2000)
            .ToList();
    }

    /// <summary>Full path of a stored report, or null when the relative path is not a report file.</summary>
    public string? PathOf(string relative)
    {
        if (!FilePattern().IsMatch(relative))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(_paths.ReportsDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(Path.GetFullPath(_paths.ReportsDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    public int PurgeOldFiles(DateTime nowUtc)
    {
        var removed = 0;
        foreach (var f in ListFiles().Where(f => nowUtc - f.CreatedUtc > TimeSpan.FromDays(FileRetentionDays)))
        {
            if (PathOf(f.Path) is { } path)
            {
                File.Delete(path);
                removed++;
            }
        }

        return removed;
    }

    public static string Slug(string text)
    {
        var slug = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "report" : slug.Length > 60 ? slug[..60] : slug;
    }
}

/// <summary>Runs due report schedules once a minute and purges old report files daily.</summary>
public sealed class ReportScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReportService _reports;
    private readonly SelfMonitorService _selfMonitor;
    private readonly ILogger<ReportScheduler> _logger;
    private DateTime _lastPurgeUtc;

    public ReportScheduler(IServiceScopeFactory scopeFactory, ReportService reports, SelfMonitorService selfMonitor, ILogger<ReportScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _reports = reports;
        _selfMonitor = selfMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Report scheduler tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        List<ReportSchedule> schedules;
        using (var scope = _scopeFactory.CreateScope())
        {
            schedules = await scope.ServiceProvider.GetRequiredService<NetWatchDbContext>().ReportSchedules.AsNoTracking().Where(s => s.IsEnabled).ToListAsync(ct);
        }

        foreach (var s in schedules.Where(s => ReportService.NextRunUtc(s, s.LastRunUtc ?? s.CreatedUtc) <= now))
        {
            await _reports.RunScheduleAsync(s.Id, ct);
        }

        // Self-monitoring: report the most recent failure of an enabled schedule.
        using (var scope = _scopeFactory.CreateScope())
        {
            var failed = await scope.ServiceProvider.GetRequiredService<NetWatchDbContext>().ReportSchedules.AsNoTracking()
                .Where(s => s.IsEnabled && s.LastStatus != null && s.LastStatus.StartsWith("Failed"))
                .OrderByDescending(s => s.LastRunUtc)
                .Select(s => s.Name + ": " + s.LastStatus)
                .FirstOrDefaultAsync(ct);
            _selfMonitor.ReportError = failed;
        }

        if (now - _lastPurgeUtc > TimeSpan.FromDays(1))
        {
            _lastPurgeUtc = now;
            var removed = _reports.PurgeOldFiles(now);
            if (removed > 0)
            {
                _logger.LogInformation("Deleted {Count} report file(s) older than {Days} days", removed, ReportService.FileRetentionDays);
            }
        }
    }
}
