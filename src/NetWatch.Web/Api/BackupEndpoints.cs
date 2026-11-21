using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using NetWatch.Core.Abstractions;
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
        admin.MapPost("/backups/portable", CreatePortableAsync).RequireRateLimiting(RateLimits.Heavy);
        admin.MapPost("/backups/{name}/restore", RestoreAsync).RequireRateLimiting(RateLimits.Heavy);
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

    public sealed record PortableBackupInput(string? Password);

    public sealed record RestoreInput(string? Confirm, string? Password);

    /// <summary>A backup whose credentials are also exported re-encrypted with a password, so it can be restored on another server.</summary>
    private static async Task<IResult> CreatePortableAsync(PortableBackupInput input, BackupService backups, HttpContext http, AuditWriter audit, CancellationToken ct)
    {
        try
        {
            var info = await backups.CreateAsync("portable", ct, input.Password ?? string.Empty);
            await audit.WriteAsync(http.Actor(), "BackupCreated", "Backup", info.Name, "portable credential export");
            return Results.Ok(info);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Security.Cryptography.CryptographicException)
        {
            return Results.Problem($"The backup failed: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>
    /// Restore from the console: validates the backup now (manifest, checksums, password), records the request and
    /// stops the service. The restore itself runs at the next start, before any database is opened; the installed
    /// service restarts by itself (recovery options), a console run must be started again.
    /// </summary>
    private static async Task<IResult> RestoreAsync(string name, RestoreInput input, BackupService backups, NetWatchPaths paths, ISecretProtector protector,
        IHostApplicationLifetime lifetime, HttpContext http, AuditWriter audit)
    {
        if (!string.Equals(input.Confirm, "RESTORE", StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = "Type RESTORE to confirm: all current data is replaced by the backup." });
        }

        if (backups.PathOf(name) is not { } path)
        {
            return Results.NotFound();
        }

        var password = string.IsNullOrEmpty(input.Password) ? null : input.Password;
        try
        {
            var manifest = BackupRestore.Validate(path, password);
            new PendingRestore(path, http.Actor().Username, DateTime.UtcNow,
                password is null ? null : Convert.ToBase64String(protector.Protect(password))).Save(paths);
            await audit.WriteAsync(http.Actor(), "RestoreRequested", "Backup", name,
                $"backup of {manifest.CreatedUtc:u} from {manifest.MachineName}{(password is null ? string.Empty : ", with portable credentials")}");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            return Results.BadRequest(new { error = $"The backup cannot be restored, nothing was changed: {ex.Message}" });
        }

        // Answer first, then stop: a non-zero exit code makes the service manager restart NetWatch (installer: failureflag 1).
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            Environment.ExitCode = 75;
            lifetime.StopApplication();
        });
        return Results.Accepted(value: new { message = "Restore scheduled. NetWatch stops now and restores the backup when it starts again (the Windows service restarts by itself within a minute)." });
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
