using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Alerts.Notifications;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data;
using NetWatch.Data.Backup;
using NetWatch.Data.Config;
using NetWatch.Web.Infrastructure;
using NetWatch.Web.Reports;

namespace NetWatch.Tests;

/// <summary>Phase 5: PDF/CSV rendering, report scheduling, backups and restore, self-checks, and reports on real databases.</summary>
public class Phase5Tests
{
    // ------------------------------------------------------------------ PDF

    private static ReportDocument SampleDocument(int rows)
    {
        var doc = new ReportDocument { Title = "Availability", Subtitle = "2026-11-01 00:00 – 2026-11-08 00:00 PKT" };
        var summary = new ReportSection { Heading = "Summary", Note = "Availability is the share of polls in which the device answered (text with (parentheses) and a \\ backslash)." };
        summary.Summary.Add(("Devices", "188"));
        summary.Summary.Add(("Average availability", "99.987 %"));
        doc.Sections.Add(summary);
        doc.Sections.Add(new ReportSection
        {
            Heading = "Least available",
            Bars = new ReportBars([("SIM-CORE-SW-01", 97.5, "97.500 %"), ("SIM-DC-01", 99.95, "99.950 %")], 100, LowIsBad: true),
        });
        doc.Sections.Add(new ReportSection
        {
            Heading = "All devices",
            Table = new ReportTable(["Device", "Type", "Availability"],
                Enumerable.Range(1, rows).Select(i => new[] { $"SIM-ACC-SW-{i:D3} – " + string.Concat(Enumerable.Repeat("a rather long device name that must be cut to fit ", 4)), "Switch", "99.9 %" }).ToList(),
                new HashSet<int> { 2 }),
        });
        return doc;
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void Pdf_is_structurally_valid_and_paginates()
    {
        var bytes = PdfReportRenderer.Render(SampleDocument(300));
        var text = Latin1(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);

        // Every xref offset must point at the start of its object.
        var xrefAt = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        var xref = text[xrefAt..];
        var offsets = Regex.Matches(xref, @"(\d{10}) 00000 n ").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        for (var i = 0; i < offsets.Count; i++)
        {
            Assert.StartsWith($"{i + 1} 0 obj", text[offsets[i]..]);
        }

        var pages = int.Parse(Regex.Match(text, @"/Type /Pages /Count (\d+)").Groups[1].Value);
        Assert.True(pages >= 4, $"300 rows need several pages, got {pages}");
        Assert.Equal(pages, Regex.Matches(text, @"/Type /Page ").Count);

        // Content streams are zlib-compressed: the last page carries the footer and a repeated table header.
        var streams = Regex.Matches(text, @"stream\n(.*?)\nendstream", RegexOptions.Singleline).Select(m => Encoding.Latin1.GetBytes(m.Groups[1].Value)).ToList();
        string Inflate(byte[] data)
        {
            using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var r = new StreamReader(z, Encoding.Latin1);
            return r.ReadToEnd();
        }

        var first = Inflate(streams[0]);
        var last = Inflate(streams[^1]);
        Assert.Contains("(Availability) Tj", first);
        Assert.Contains("\\(parentheses\\)", first);
        Assert.Contains($"(Page {pages} of {pages}) Tj", last);
        Assert.Contains("(Device) Tj", last);
        Assert.Contains("\x85", first); // long names end with an ellipsis (WinAnsi 0x85)
    }

    [Fact]
    public void Text_metrics_fit_and_encode()
    {
        var fitted = PdfWriter.Fit("A very long interface description that does not fit", 80, 8);
        Assert.EndsWith("…", fitted);
        Assert.True(PdfWriter.Width(fitted, 8) <= 80);
        Assert.Equal("short", PdfWriter.Fit("short", 80, 8));
        Assert.True(PdfWriter.Width("WWW", 10, bold: true) > PdfWriter.Width("iii", 10));
        Assert.Equal(new byte[] { 0x96, 0x80, (byte)'?', 0xE9 }, PdfWriter.WinAnsi("–€Ωé"));
        Assert.All(PdfWriter.Wrap("one two three four five six seven eight nine ten", 60, 9), l => Assert.True(PdfWriter.Width(l, 9) <= 60));
    }

    [Fact]
    public void Csv_has_bom_sections_and_formula_protection()
    {
        var doc = SampleDocument(2);
        doc.Sections.Add(new ReportSection { Heading = "Injection", Table = new ReportTable(["Value"], [["=HYPERLINK(\"x\")"], ["-5"], ["@cmd"]]) });
        var bytes = CsvReportRenderer.Render(doc);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Contains("## All devices", text);
        Assert.Contains("Average availability,99.987 %", text);
        Assert.Contains("'=HYPERLINK", text);
        Assert.Contains("\n-5", text.Replace("\r\n", "\n"));
        Assert.Contains("'@cmd", text);
    }

    // ------------------------------------------------------------------ scheduling

    [Fact]
    public void Scheduled_report_periods_follow_pakistan_days()
    {
        var run = new DateTime(2026, 11, 21, 2, 0, 0, DateTimeKind.Utc); // Sat 07:00 PKT
        Assert.Equal((new DateTime(2026, 11, 19, 19, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 20, 19, 0, 0, DateTimeKind.Utc)), ReportService.PeriodFor(ReportFrequency.Daily, run));
        Assert.Equal(new DateTime(2026, 11, 13, 19, 0, 0, DateTimeKind.Utc), ReportService.PeriodFor(ReportFrequency.Weekly, run).FromUtc);
        Assert.Equal((new DateTime(2026, 9, 30, 19, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 31, 19, 0, 0, DateTimeKind.Utc)), ReportService.PeriodFor(ReportFrequency.Monthly, run));

        var weekly = new ReportSchedule { Frequency = ReportFrequency.Weekly, DayOfWeek = 1, TimeOfDay = "07:00" };
        Assert.Equal(new DateTime(2026, 11, 23, 2, 0, 0, DateTimeKind.Utc), ReportService.NextRunUtc(weekly, run)); // Mon 07:00 PKT
        var daily = new ReportSchedule { Frequency = ReportFrequency.Daily, TimeOfDay = "07:00" };
        Assert.Equal(new DateTime(2026, 11, 22, 2, 0, 0, DateTimeKind.Utc), ReportService.NextRunUtc(daily, run)); // strictly after
        var monthly = new ReportSchedule { Frequency = ReportFrequency.Monthly, DayOfMonth = 1, TimeOfDay = "06:30" };
        Assert.Equal(new DateTime(2026, 12, 1, 1, 30, 0, DateTimeKind.Utc), ReportService.NextRunUtc(monthly, run));

        Assert.Equal(new DateTime(2026, 11, 21, 20, 30, 0, DateTimeKind.Utc), BackupService.NextRunUtc(new BackupSettings { DailyAt = "01:30" }, run));
        Assert.Equal("weekly-availability-management", ReportService.Slug("Weekly availability (management)!"));
    }

    [Fact]
    public void Backup_folder_validation()
    {
        Assert.Null(new BackupSettings().ValidateFolder());
        Assert.Null(new BackupSettings { Folder = @"E:\Backups\NetWatch" }.ValidateFolder());
        Assert.Null(new BackupSettings { Folder = @"\\backupserver\netwatch" }.ValidateFolder());
        Assert.NotNull(new BackupSettings { Folder = @"backups" }.ValidateFolder());
        Assert.NotNull(new BackupSettings { Folder = @"E:\Backups\..\Windows" }.ValidateFolder());
        Assert.True(BackupService.IsValidName("netwatch-backup-20261121-013000.zip"));
        Assert.True(BackupService.IsValidName("netwatch-backup-20261121-013000-manual.zip"));
        Assert.False(BackupService.IsValidName("..\\netwatch-backup-20261121-013000.zip"));
        Assert.False(BackupService.IsValidName("netwatch.db"));
    }

    // ------------------------------------------------------------------ self-checks

    private static SelfCheckInput Healthy(DateTime now) => new(
        now, now.AddDays(-3), 200L << 30, 500L << 30, 188, 2, 0, 0, 10, 0, 0, 0,
        now.AddDays(300), true, now.AddHours(-5), null, null, [], false, null);

    [Fact]
    public void Self_checks_detect_problems()
    {
        var now = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
        var s = new SelfMonitorSettings();
        Assert.All(SelfChecks.Evaluate(Healthy(now), s), r => Assert.True(r.Ok, $"{r.Name}: {r.Detail}"));

        SelfCheckResult Check(SelfCheckInput input, string name) => Assert.Single(SelfChecks.Evaluate(input, s), r => r.Name == name);
        Assert.False(Check(Healthy(now) with { DiskFreeBytes = 3L << 30 }, SelfChecks.Disk).Ok);
        Assert.True(Check(Healthy(now) with { PollsQueued = 120, ConsecutiveBacklogMinutes = 2 }, SelfChecks.Polling).Ok);
        Assert.False(Check(Healthy(now) with { PollsQueued = 120, ConsecutiveBacklogMinutes = 3 }, SelfChecks.Polling).Ok);
        Assert.False(Check(Healthy(now) with { MetricsDroppedDelta = 5 }, SelfChecks.MetricStorage).Ok);
        Assert.False(Check(Healthy(now) with { ReceiverDroppedDelta = 1000 }, SelfChecks.Receivers).Ok);
        Assert.False(Check(Healthy(now) with { CertificateNotAfterUtc = now.AddDays(10) }, SelfChecks.Certificate).Ok);
        Assert.False(Check(Healthy(now) with { NewestBackupUtc = now.AddHours(-40) }, SelfChecks.Backups).Ok);
        Assert.False(Check(Healthy(now) with { BackupError = "disk full" }, SelfChecks.Backups).Ok);
        Assert.True(Check(Healthy(now) with { NewestBackupUtc = null, StartedUtc = now.AddHours(-2) }, SelfChecks.Backups).Ok); // first backup not due yet
        Assert.False(Check(Healthy(now) with { NewestBackupUtc = null }, SelfChecks.Backups).Ok);
        Assert.False(Check(Healthy(now) with { ReportError = "Weekly: SMTP refused" }, SelfChecks.Reports).Ok);
        Assert.False(Check(Healthy(now) with { Notifications = [new ChannelStatus("Smtp", 1, 1, now.AddHours(-3), now.AddMinutes(-5), "relay down")] }, SelfChecks.Notifications).Ok);
        Assert.True(Check(Healthy(now) with { Notifications = [new ChannelStatus("Smtp", 2, 1, now.AddMinutes(-1), now.AddMinutes(-5), "relay down")] }, SelfChecks.Notifications).Ok);
        Assert.False(Check(Healthy(now) with { ForwardingEnabled = true, ForwardingError = "access denied" }, SelfChecks.Forwarding).Ok);
        Assert.DoesNotContain(SelfChecks.Evaluate(Healthy(now) with { BackupEnabled = false }, s), r => r.Name == SelfChecks.Backups);
    }

    // ------------------------------------------------------------------ real databases: backup, restore, reports

    private sealed class TempData : IDisposable
    {
        public TempData()
        {
            Root = Path.Combine(Path.GetTempPath(), "netwatch-test-" + Guid.NewGuid().ToString("N"));
            Paths = new NetWatchPaths(Root);
            Paths.EnsureCreated();
        }

        public string Root { get; }
        public NetWatchPaths Paths { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<ServiceProvider> StartAsync(NetWatchPaths paths)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(paths);
        var sp = services.BuildServiceProvider();
        await sp.InitialiseDatabasesAsync(NullLogger.Instance);
        return sp;
    }

    [Fact]
    public async Task Backup_and_restore_round_trip_with_checks()
    {
        using var temp = new TempData();
        await using var sp = await StartAsync(temp.Paths);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            db.Devices.Add(new Device { Name = "BACKED-UP", IpAddress = "10.0.0.1", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, StatusSinceUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await File.WriteAllTextAsync(temp.Paths.SettingsFile, "{\"Https\":{\"Port\":8443}}");
        var backups = sp.GetRequiredService<BackupService>();
        var info = await backups.CreateAsync("manual", CancellationToken.None);
        Assert.Matches("^netwatch-backup-\\d{8}-\\d{6}-manual\\.zip$", info.Name);
        Assert.True(info.IncludesMetrics);
        Assert.Single(backups.List());
        Assert.Null(backups.LastError);
        var zipPath = backups.PathOf(info.Name)!;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.NotNull(zip.GetEntry("data/netwatch.db"));
            Assert.NotNull(zip.GetEntry("data/events.db"));
            Assert.NotNull(zip.GetEntry("data/metrics.db"));
            Assert.NotNull(zip.GetEntry("appsettings.json"));
        }

        // Change the live data after the backup, then restore: the device count goes back.
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            db.Devices.Add(new Device { Name = "AFTER-BACKUP", IpAddress = "10.0.0.2", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, StatusSinceUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        SqliteConnection.ClearAllPools();
        var message = BackupRestore.Restore(zipPath, temp.Paths);
        Assert.Contains("Restored backup", message);
        Assert.Single(Directory.GetDirectories(temp.Root, "before-restore-*"));
        using (var connection = new SqliteConnection(SqliteConnectionFactory.Build(temp.Paths.ConfigDb)))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT group_concat(Name) FROM Devices";
            Assert.Equal("BACKED-UP", cmd.ExecuteScalar());
        }

        SqliteConnection.ClearAllPools();

        // A damaged file is rejected before anything is moved.
        var damaged = Path.Combine(temp.Root, "damaged.zip");
        File.Copy(zipPath, damaged);
        using (var zip = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            zip.GetEntry("appsettings.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("appsettings.json").Open());
            writer.Write("{\"tampered\":true}");
        }

        var error = Assert.Throws<InvalidDataException>(() => BackupRestore.Restore(damaged, temp.Paths));
        Assert.Contains("Checksum mismatch", error.Message);

        // Entries outside the allow-list (zip slip) are refused.
        var evil = Path.Combine(temp.Root, "evil.zip");
        File.Copy(zipPath, evil);
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../outside.txt").Open());
            writer.Write("x");
        }

        Assert.Throws<InvalidDataException>(() => BackupRestore.Restore(evil, temp.Paths));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(temp.Root)!, "outside.txt")));
        Assert.Single(Directory.GetDirectories(temp.Root, "before-restore-*"));
    }

    [Fact]
    public async Task Every_report_renders_on_real_databases()
    {
        using var temp = new TempData();
        await using var sp = await StartAsync(temp.Paths);
        var now = DateTime.UtcNow;
        int deviceId;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var device = new Device { Name = "CORE-SW-01", IpAddress = "10.0.0.1", Type = DeviceType.Switch, Vendor = "Cisco", CreatedUtc = now, UpdatedUtc = now, StatusSinceUtc = now };
            var dc = new Device { Name = "DC-01", IpAddress = "10.0.0.10", Type = DeviceType.DomainController, CreatedUtc = now, UpdatedUtc = now, StatusSinceUtc = now };
            db.Devices.AddRange(device, dc);
            await db.SaveChangesAsync();
            deviceId = device.Id;
            db.Alerts.Add(new Alert { DeviceId = device.Id, DedupKey = "r1|x", Severity = Severity.Major, Title = "High CPU: CORE-SW-01", Message = "CPU 95 %", FirstSeenUtc = now.AddHours(-2), LastSeenUtc = now.AddHours(-1), AckUtc = now.AddHours(-1.5), AckByUsername = "admin", ResolvedUtc = now.AddHours(-1), State = AlertState.Resolved });
            db.Volumes.Add(new Volume { DeviceId = dc.Id, Name = "C:", TotalBytes = 100L << 30, FreeBytes = 10L << 30, LastSeenUtc = now });
            await db.SaveChangesAsync();

            var events = scope.ServiceProvider.GetRequiredService<NetWatch.Data.Events.EventsDbContext>();
            events.DcSecurityEvents.Add(new DcSecurityEvent { DeviceId = dc.Id, TimestampUtc = now.AddHours(-1), EventId = 4728, RecordId = 1, Category = "Group", GroupName = "Domain Admins", TargetUser = "j.doe", SubjectUser = "adm", Severity = Severity.Critical, Message = "added" });
            events.DcSecurityEvents.Add(new DcSecurityEvent { DeviceId = dc.Id, TimestampUtc = now.AddHours(-1), EventId = 4625, RecordId = 2, Category = "LogonFailure", TargetUser = "j.doe", SourceIp = "10.1.1.1", Severity = Severity.Warning, Message = "failed" });
            await events.SaveChangesAsync();
        }

        var metrics = sp.GetRequiredService<IMetricStore>();
        var ts = new DateTimeOffset(now.AddMinutes(-30)).ToUnixTimeMilliseconds();
        metrics.Enqueue(new NetWatch.Core.Metrics.DeviceSample(deviceId, ts, true, 1.2, 0, 40, 50, null));
        metrics.Enqueue(new NetWatch.Core.Metrics.DeviceSample(deviceId, ts + 60_000, false, null, 100, null, null, null));
        sp.GetRequiredService<NetWatch.Data.Metrics.SqliteMetricStore>().Flush();

        var mail = new MailSender(sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<ISecretProtector>());
        var reports = new ReportService(sp.GetRequiredService<IServiceScopeFactory>(), temp.Paths, mail, NullLogger<ReportService>.Instance);
        foreach (var type in Enum.GetValues<ReportType>())
        {
            var request = new ReportRequest(type, now.AddDays(-1), now.AddMinutes(1));
            var (pdf, pdfName, _) = await reports.GenerateAsync(request, ReportFormat.Pdf, CancellationToken.None);
            Assert.StartsWith("%PDF", Latin1(pdf[..8]));
            Assert.EndsWith(".pdf", pdfName);
            var (csv, _, mime) = await reports.GenerateAsync(request, ReportFormat.Csv, CancellationToken.None);
            Assert.Equal("text/csv", mime);
            var text = Encoding.UTF8.GetString(csv);
            switch (type)
            {
                case ReportType.Availability:
                    Assert.Contains("CORE-SW-01", text);
                    Assert.Contains("50.000 %", text); // one poll up, one down
                    break;
                case ReportType.Alerts:
                    Assert.Contains("High CPU: CORE-SW-01", text);
                    Assert.Contains("Mean time to acknowledge,30 min", text);
                    break;
                case ReportType.Capacity:
                    Assert.Contains("C:", text);
                    Assert.Contains("90.0 %", text);
                    break;
                case ReportType.Security:
                    Assert.Contains("Domain Admins", text);
                    Assert.Contains("Failed logons,1", text);
                    break;
                case ReportType.Inventory:
                    Assert.Contains("Cisco", text);
                    break;
            }
        }

        // A schedule run saves the file and records the status (no SMTP configured → not e-mailed).
        int scheduleId;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var s = new ReportSchedule { Name = "Daily inventory", ReportType = ReportType.Inventory, Format = ReportFormat.Pdf, Frequency = ReportFrequency.Daily, EmailTo = "noc@corp.local", CreatedUtc = now, UpdatedUtc = now };
            db.ReportSchedules.Add(s);
            await db.SaveChangesAsync();
            scheduleId = s.Id;
        }

        var status = await reports.RunScheduleAsync(scheduleId, CancellationToken.None);
        Assert.StartsWith("OK: saved", status);
        Assert.Contains("e-mail not sent", status);
        var file = Assert.Single(reports.ListFiles());
        Assert.NotNull(reports.PathOf(file.Path));
        Assert.Null(reports.PathOf("../netwatch.db"));
        Assert.Null(reports.PathOf("2026-11/..\\..\\data\\netwatch.db"));
    }
}
