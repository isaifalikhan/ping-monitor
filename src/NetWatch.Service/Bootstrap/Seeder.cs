using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Alerts;
using NetWatch.Collectors.Simulator;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;

namespace NetWatch.Service.Bootstrap;

/// <summary>First-start data: administrator account, built-in alert rules, default settings, optional simulator inventory.</summary>
public static class Seeder
{
    public static async Task SeedAsync(IServiceProvider services, bool simulate, ILogger logger, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var paths = scope.ServiceProvider.GetRequiredService<NetWatchPaths>();
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var now = DateTime.UtcNow;

        if (!await db.Users.AnyAsync(ct))
        {
            var password = PasswordService.Generate();
            var admin = new User
            {
                Username = "admin",
                DisplayName = "Administrator",
                Role = UserRole.Admin,
                AuthSource = AuthSource.Local,
                IsEnabled = true,
                MustChangePassword = true,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            admin.PasswordHash = passwords.Hash(admin, password);
            db.Users.Add(admin);
            await db.SaveChangesAsync(ct);
            WritePasswordFile(paths, password);
            logger.LogWarning("Created the initial 'admin' account. Its one-time password is in {File} (readable by Administrators only)", paths.InitialAdminPasswordFile);
        }

        // Built-in rules: add any that are missing (also after upgrades that introduce new built-in rules).
        var existingKeys = await db.AlertRules.Where(r => r.IsBuiltIn).Select(r => r.Name).ToListAsync(ct);
        var missing = DefaultRules.Create(now).Where(r => !existingKeys.Contains(r.Name)).ToList();
        if (missing.Count > 0)
        {
            db.AlertRules.AddRange(missing);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Added {Count} built-in alert rule(s)", missing.Count);
        }

        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        foreach (var (key, value) in new (string, object)[]
        {
            (MonitoringSettings.Key, new MonitoringSettings()),
            (SecuritySettings.Key, new SecuritySettings()),
            (RetentionSettings.Key, new RetentionSettings()),
            (AlertingSettings.Key, new AlertingSettings()),
            (ReceiverSettings.Key, new ReceiverSettings()),
            (ServerMonitoringSettings.Key, new ServerMonitoringSettings()),
            (NotificationSettings.Key, new NotificationSettings()),
            (LdapSettings.Key, new LdapSettings()),
            (VmwareSettings.Key, new VmwareSettings()),
        })
        {
            if (!await db.Settings.AnyAsync(s => s.Key == key, ct))
            {
                await settings.SaveAsync(key, value, null, ct);
            }
        }

        if (simulate)
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            await SimulatorSeeder.SeedAsync(db, protector, logger, ct);

            // Inventories seeded by an earlier version get the server-monitoring additions.
            await SimulatorSeeder.UpgradeAsync(db, protector, logger, ct);
            await SimulatorSeeder.UpgradeVmwareAsync(db, protector, logger, ct);
        }
    }

    /// <summary>Generates a new admin password (CLI --reset-admin). Re-enables and unlocks the account.</summary>
    public static async Task<string> ResetAdminAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var paths = scope.ServiceProvider.GetRequiredService<NetWatchPaths>();
        var now = DateTime.UtcNow;
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Username == "admin", ct);
        if (admin is null)
        {
            admin = new User { Username = "admin", DisplayName = "Administrator", CreatedUtc = now };
            db.Users.Add(admin);
        }

        var password = PasswordService.Generate();
        admin.Role = UserRole.Admin;
        admin.AuthSource = AuthSource.Local;
        admin.IsEnabled = true;
        admin.LockedUntilUtc = null;
        admin.FailedLoginCount = 0;
        admin.MustChangePassword = true;
        admin.UpdatedUtc = now;
        admin.PasswordHash = passwords.Hash(admin, password);
        await db.SaveChangesAsync(ct);
        await db.UserSessions.Where(s => s.UserId == admin.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, now), ct);
        db.AuditLog.Add(new AuditEntry { TimestampUtc = now, Username = "system", Action = "AdminPasswordReset", TargetType = "User", TargetId = admin.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), Details = "via --reset-admin" });
        await db.SaveChangesAsync(ct);
        WritePasswordFile(paths, password);
        return password;
    }

    private static void WritePasswordFile(NetWatchPaths paths, string password) =>
        SecureFile.WriteRestricted(
            paths.InitialAdminPasswordFile,
            $"NetWatch initial administrator account{Environment.NewLine}" +
            $"User name: admin{Environment.NewLine}" +
            $"Password:  {password}{Environment.NewLine}{Environment.NewLine}" +
            $"You must change this password at first sign-in. Delete this file afterwards.{Environment.NewLine}");
}
