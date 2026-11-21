using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Alerts.Notifications;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;
using NetWatch.Web.Reports;

namespace NetWatch.Web.Api;

public sealed record RunReportInput(ReportType Type, ReportFormat Format, DateTime? FromUtc, DateTime? ToUtc, int? GroupId);

public sealed record ScheduleInput(
    string? Name, bool IsEnabled, ReportType ReportType, ReportFormat Format, ReportFrequency Frequency,
    string? TimeOfDay, int DayOfWeek, int DayOfMonth, int? GroupId, string? EmailTo);

public sealed record ScheduleDto(
    int Id, string Name, bool IsEnabled, ReportType ReportType, ReportFormat Format, ReportFrequency Frequency, string TimeOfDay,
    int DayOfWeek, int DayOfMonth, int? GroupId, string? EmailTo, DateTime? LastRunUtc, string? LastStatus, string? LastFile,
    DateTime NextRunUtc, string? CreatedBy);

/// <summary>Reports: on-demand PDF/CSV, stored report files and report schedules.</summary>
public static class ReportEndpoints
{
    private const int MaxPeriodDays = 366;

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/reports").RequireAuthorization(Policies.Viewer);
        read.MapGet("/types", () => Results.Ok(ReportCatalog.Types.Select(t => new { type = t.Type.ToString(), name = t.Name, description = t.Description, hasPeriod = t.HasPeriod })));
        read.MapPost("/run", RunAsync).RequireRateLimiting(RateLimits.Heavy);
        read.MapGet("/files", (ReportService reports) => Results.Ok(reports.ListFiles()));
        read.MapGet("/files/{month}/{file}", DownloadAsync);
        read.MapGet("/schedules", ListSchedulesAsync);

        var write = app.MapGroup("/api/reports").RequireAuthorization(Policies.Operator);
        write.MapDelete("/files/{month}/{file}", DeleteFileAsync);
        write.MapPost("/schedules", CreateScheduleAsync);
        write.MapPut("/schedules/{id:int}", UpdateScheduleAsync);
        write.MapDelete("/schedules/{id:int}", DeleteScheduleAsync);
        write.MapPost("/schedules/{id:int}/run", RunScheduleNowAsync).RequireRateLimiting(RateLimits.Heavy);
    }

    private static async Task<IResult> RunAsync(RunReportInput input, ReportService reports, NetWatchDbContext db, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        var to = input.ToUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        var from = input.FromUtc?.ToUniversalTime() ?? to.AddDays(-7);
        if (from >= to || (to - from).TotalDays > MaxPeriodDays)
        {
            return Results.BadRequest(new { error = $"The period must be between a few minutes and {MaxPeriodDays} days." });
        }

        if (input.GroupId is { } gid && !await db.Groups.AnyAsync(g => g.Id == gid, ct))
        {
            return Results.BadRequest(new { error = "The selected group does not exist." });
        }

        var (content, name, mime) = await reports.GenerateAsync(new ReportRequest(input.Type, from, to, input.GroupId), input.Format, ct);
        await audit.WriteAsync(http.Actor(), "ReportGenerated", "Report", input.Type.ToString(),
            string.Create(CultureInfo.InvariantCulture, $"{input.Format}, {from:u} – {to:u}{(input.GroupId is { } g ? $", group {g}" : string.Empty)}"));
        return Results.File(content, mime, name);
    }

    private static IResult DownloadAsync(string month, string file, ReportService reports) =>
        reports.PathOf($"{month}/{file}") is { } path
            ? Results.File(path, file.EndsWith(".pdf", StringComparison.Ordinal) ? "application/pdf" : "text/csv", file)
            : Results.NotFound();

    private static async Task<IResult> DeleteFileAsync(string month, string file, ReportService reports, HttpContext http, AuditWriter audit)
    {
        if (reports.PathOf($"{month}/{file}") is not { } path)
        {
            return Results.NotFound();
        }

        File.Delete(path);
        await audit.WriteAsync(http.Actor(), "ReportFileDeleted", "Report", $"{month}/{file}");
        return Results.NoContent();
    }

    private static ScheduleDto ToDto(ReportSchedule s) => new(
        s.Id, s.Name, s.IsEnabled, s.ReportType, s.Format, s.Frequency, s.TimeOfDay, s.DayOfWeek, s.DayOfMonth, s.GroupId, s.EmailTo,
        s.LastRunUtc, s.LastStatus, s.LastFile, ReportService.NextRunUtc(s, s.LastRunUtc is { } last && last > s.CreatedUtc ? last : s.CreatedUtc > DateTime.UtcNow ? s.CreatedUtc : DateTime.UtcNow), s.CreatedBy);

    private static async Task<IResult> ListSchedulesAsync(NetWatchDbContext db, CancellationToken ct) =>
        Results.Ok((await db.ReportSchedules.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).Select(ToDto));

    private static async Task<string?> ValidateAsync(ScheduleInput input, NetWatchDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 100)
        {
            return "A name is required (max 100 characters).";
        }

        if (!Enum.IsDefined(input.ReportType) || !Enum.IsDefined(input.Format) || !Enum.IsDefined(input.Frequency))
        {
            return "Unknown report type, format or frequency.";
        }

        if (!TimeSpan.TryParseExact(input.TimeOfDay ?? string.Empty, @"hh\:mm", CultureInfo.InvariantCulture, out _))
        {
            return "The time must be HH:mm (local time, PKT).";
        }

        if (input.DayOfWeek is < 0 or > 6 || input.DayOfMonth is < 1 or > 28)
        {
            return "Day of week must be 0–6 (Sunday–Saturday) and day of month 1–28.";
        }

        if (!string.IsNullOrWhiteSpace(input.EmailTo) && !MailSender.ValidRecipients(input.EmailTo, out _))
        {
            return "E-mail recipients must be valid addresses separated by commas (max 20).";
        }

        if (input.GroupId is { } gid && !await db.Groups.AnyAsync(g => g.Id == gid, ct))
        {
            return "The selected group does not exist.";
        }

        return null;
    }

    private static void Apply(ReportSchedule s, ScheduleInput input)
    {
        s.Name = input.Name!.Trim();
        s.IsEnabled = input.IsEnabled;
        s.ReportType = input.ReportType;
        s.Format = input.Format;
        s.Frequency = input.Frequency;
        s.TimeOfDay = input.TimeOfDay!;
        s.DayOfWeek = input.DayOfWeek;
        s.DayOfMonth = input.DayOfMonth;
        s.GroupId = input.GroupId;
        s.EmailTo = string.IsNullOrWhiteSpace(input.EmailTo) ? null : input.EmailTo.Trim();
        s.UpdatedUtc = DateTime.UtcNow;
    }

    private static async Task<IResult> CreateScheduleAsync(ScheduleInput input, NetWatchDbContext db, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        if (await ValidateAsync(input, db, ct) is { } error)
        {
            return Results.BadRequest(new { error });
        }

        if (await db.ReportSchedules.CountAsync(ct) >= 200)
        {
            return Results.BadRequest(new { error = "At most 200 report schedules." });
        }

        var s = new ReportSchedule { CreatedUtc = DateTime.UtcNow, CreatedBy = http.User.Username() };
        Apply(s, input);
        db.ReportSchedules.Add(s);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(http.Actor(), "ReportScheduleCreated", "ReportSchedule", s.Id.ToString(CultureInfo.InvariantCulture), $"{s.Name}: {s.ReportType} {s.Frequency} {s.Format} → {s.EmailTo ?? "file only"}");
        return Results.Ok(ToDto(s));
    }

    private static async Task<IResult> UpdateScheduleAsync(int id, ScheduleInput input, NetWatchDbContext db, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        var s = await db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
        {
            return Results.NotFound();
        }

        if (await ValidateAsync(input, db, ct) is { } error)
        {
            return Results.BadRequest(new { error });
        }

        Apply(s, input);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(http.Actor(), "ReportScheduleUpdated", "ReportSchedule", id.ToString(CultureInfo.InvariantCulture), $"{s.Name}: {s.ReportType} {s.Frequency} {s.Format} → {s.EmailTo ?? "file only"}");
        return Results.Ok(ToDto(s));
    }

    private static async Task<IResult> DeleteScheduleAsync(int id, NetWatchDbContext db, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        var s = await db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
        {
            return Results.NotFound();
        }

        db.ReportSchedules.Remove(s);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(http.Actor(), "ReportScheduleDeleted", "ReportSchedule", id.ToString(CultureInfo.InvariantCulture), s.Name);
        return Results.NoContent();
    }

    private static async Task<IResult> RunScheduleNowAsync(int id, ReportService reports, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        try
        {
            var status = await reports.RunScheduleAsync(id, ct);
            await audit.WriteAsync(http.Actor(), "ReportScheduleRun", "ReportSchedule", id.ToString(CultureInfo.InvariantCulture), status);
            return Results.Ok(new { status });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }
}
