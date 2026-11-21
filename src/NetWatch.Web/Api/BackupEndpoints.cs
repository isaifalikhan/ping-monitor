using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetWatch.Core.Configuration;
using NetWatch.Data.Backup;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Api;

/// <summary>Backups (admin only): list, create now, download, delete, and the backup / self-monitoring settings.</summary>
public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api").RequireAuthorization(Policies.Admin);
        admin.MapGet("/backups", List);
        admin.MapPost("/backups", CreateAsync).RequireRateLimiting(RateLimits.Heavy);
        admin.MapGet("/backups/{name}", DownloadAsync);
        admin.MapDelete("/backups/{name}", DeleteAsync);
        admin.MapPut("/settings/backup", SaveBackupSettingsAsync);
        admin.MapPut("/settings/selfmonitor", SaveSelfMonitorAsync);
    }

    private static IResult List(BackupService backups, SettingsService settings)
    {
        var s = settings.Backup;
        return Results.Ok(new
        {
            items = backups.List(),
            settings = s,
            defaultFolder = backups.TargetFolder,
            nextRunUtc = s.Enabled ? BackupService.NextRunUtc(s, DateTime.UtcNow) : (DateTime?)null,
            lastAttemptUtc = backups.LastAttemptUtc,
            lastError = backups.LastError,
            running = backups.Running,
        });
    }

    private static async Task<IResult> CreateAsync(BackupService backups, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        try
        {
            var info = await backups.CreateAsync("manual", ct);
            await audit.WriteAsync(http.Actor(), "BackupCreated", "Backup", info.Name);
            return Results.Ok(info);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return Results.Problem($"The backup failed: {ex.Message}", statusCode: 500);
        }
    }

    private static async Task<IResult> DownloadAsync(string name, BackupService backups, HttpContext http, AuditWriter audit)
    {
        if (backups.PathOf(name) is not { } path)
        {
            return Results.NotFound();
        }

        // Backups contain the (encrypted) credentials and the user database: every download is audited.
        await audit.WriteAsync(http.Actor(), "BackupDownloaded", "Backup", name);
        return Results.File(path, "application/zip", name, enableRangeProcessing: true);
    }

    private static async Task<IResult> DeleteAsync(string name, BackupService backups, HttpContext http, AuditWriter audit)
    {
        if (!backups.Delete(name))
        {
            return Results.NotFound();
        }

        await audit.WriteAsync(http.Actor(), "BackupDeleted", "Backup", name);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveBackupSettingsAsync(BackupSettings value, HttpContext http, SettingsService settings, AuditWriter audit, CancellationToken ct)
    {
        value.Normalise();
        if (value.ValidateFolder() is { } error)
        {
            return Results.BadRequest(new { error });
        }

        if (value.Folder is { } folder)
        {
            try
            {
                // The service account must be able to write there; find out now rather than at 01:30.
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, $".netwatch-write-test-{Guid.NewGuid():N}");
                await File.WriteAllTextAsync(probe, "ok", ct);
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = $"NetWatch cannot write to {folder}: {ex.Message}" });
            }
        }

        await settings.SaveAsync(BackupSettings.Key, value, http.User.UserId(), ct);
        await audit.WriteAsync(http.Actor(), "SettingsUpdated", "Settings", BackupSettings.Key, System.Text.Json.JsonSerializer.Serialize(value));
        return Results.Ok(value);
    }

    private static async Task<IResult> SaveSelfMonitorAsync(SelfMonitorSettings value, HttpContext http, SettingsService settings, AuditWriter audit, CancellationToken ct)
    {
        value.Normalise();
        await settings.SaveAsync(SelfMonitorSettings.Key, value, http.User.UserId(), ct);
        await audit.WriteAsync(http.Actor(), "SettingsUpdated", "Settings", SelfMonitorSettings.Key, System.Text.Json.JsonSerializer.Serialize(value));
        return Results.Ok(value);
    }
}
