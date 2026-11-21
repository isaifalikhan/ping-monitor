using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Credentials;
using NetWatch.Core.Entities;
using NetWatch.Data;
using NetWatch.Data.Backup;
using NetWatch.Data.Config;

namespace NetWatch.Tests;

/// <summary>Tests that move database files: not run in parallel with tests that clear the shared SQLite pool.</summary>
[CollectionDefinition(nameof(SqliteFileMoves), DisableParallelization = true)]
public sealed class SqliteFileMoves;

/// <summary>WP8: portable credential export, restore with re-encryption, wrong passwords, pending restores, damaged backups.</summary>
[Collection(nameof(SqliteFileMoves))]
public sealed class Wp8BackupTests : IAsyncLifetime
{
    private const string Password = "Pilot-Restore-2026!";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-wp8-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;
    private NetWatchPaths _paths = null!;

    public async Task InitializeAsync()
    {
        _paths = new NetWatchPaths(_root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(_paths);
        _sp = services.BuildServiceProvider();
        await _sp.InitialiseDatabasesAsync(NullLogger.Instance);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var now = DateTime.UtcNow;
        db.CredentialProfiles.Add(new CredentialProfile { Name = "Core SNMP", Kind = CredentialKind.SnmpV3, DisplayHint = "x", CreatedUtc = now, UpdatedUtc = now,
            SecretBlob = protector.Protect(new CredentialSecret { Username = "nwro", AuthPassword = "auth-secret-1", PrivPassword = "priv-secret-1" }.ToJson()) });
        db.CredentialProfiles.Add(new CredentialProfile { Name = "Servers", Kind = CredentialKind.Windows, DisplayHint = "x", CreatedUtc = now, UpdatedUtc = now,
            SecretBlob = protector.Protect(new CredentialSecret { Username = "svc-netwatch", Domain = "CORP", Password = "W1ndows-secret" }.ToJson()) });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Portable_backup_restores_credentials_with_the_password()
    {
        var backups = _sp.GetRequiredService<BackupService>();
        var info = await backups.CreateAsync("portable", CancellationToken.None, Password);
        Assert.EndsWith("-portable.zip", info.Name);
        var zipPath = backups.PathOf(info.Name)!;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var text = new StreamReader(zip.GetEntry(PortableSecrets.FileName)!.Open()).ReadToEnd();
            Assert.DoesNotContain("W1ndows-secret", text);   // never in clear text
            Assert.Contains("PBKDF2-SHA256", text);
        }

        Assert.NotNull(BackupRestore.Validate(zipPath, Password));
        Assert.Throws<CryptographicException>(() => BackupRestore.Validate(zipPath, "wrong-password-123"));

        // Simulate the original server's secrets being unreadable (as they are on a new machine), then restore.
        await _sp.DisposeAsync();
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection(SqliteConnectionFactory.Build(_paths.ConfigDb)))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE CredentialProfiles SET SecretBlob = x'00'";
            cmd.ExecuteNonQuery();
        }

        var message = BackupRestore.Restore(zipPath, _paths, Password, new Data.Security.DpapiSecretProtector());
        Assert.Contains("2 credential(s) were re-encrypted", message);
        Assert.DoesNotContain("re-enter all credential secrets", message);

        SqliteConnection.ClearAllPools();
        using var db = new NetWatchDbContext(new DbContextOptionsBuilder<NetWatchDbContext>().UseSqlite(SqliteConnectionFactory.Build(_paths.ConfigDb)).Options);
        var protector = new Data.Security.DpapiSecretProtector();
        var secrets = db.CredentialProfiles.AsNoTracking().OrderBy(p => p.Id).ToList().Select(p => CredentialSecret.FromJson(protector.Unprotect(p.SecretBlob))).ToList();
        Assert.Equal("priv-secret-1", secrets[0].PrivPassword);
        Assert.Equal("W1ndows-secret", secrets[1].Password);
        _sp = new ServiceCollection().BuildServiceProvider(); // disposed above; keep DisposeAsync happy
    }

    [Fact]
    public async Task Wrong_password_changes_nothing()
    {
        var backups = _sp.GetRequiredService<BackupService>();
        var info = await backups.CreateAsync("portable", CancellationToken.None, Password);
        await _sp.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_paths.ConfigDb)));
        Assert.Throws<CryptographicException>(() => BackupRestore.Restore(backups.PathOf(info.Name)!, _paths, "not-the-password!", new Data.Security.DpapiSecretProtector()));
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_paths.ConfigDb))));
        Assert.Empty(Directory.EnumerateDirectories(_paths.Root, "before-restore-*"));
        _sp = new ServiceCollection().BuildServiceProvider();
    }

    [Fact]
    public async Task Export_password_must_be_long_enough()
    {
        var backups = _sp.GetRequiredService<BackupService>();
        await Assert.ThrowsAsync<ArgumentException>(() => backups.CreateAsync("portable", CancellationToken.None, "short"));
    }

    [Fact]
    public async Task Pending_restore_runs_once_at_start_up()
    {
        var backups = _sp.GetRequiredService<BackupService>();
        var info = await backups.CreateAsync("manual", CancellationToken.None);
        var protector = new Data.Security.DpapiSecretProtector();
        new PendingRestore(backups.PathOf(info.Name)!, "admin", DateTime.UtcNow, null).Save(_paths);
        await _sp.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var outcome = PendingRestore.RunIfPending(_paths, protector);
        Assert.NotNull(outcome);
        Assert.StartsWith("Restored backup of", outcome);
        Assert.Contains("requested by admin", outcome);
        Assert.False(File.Exists(PendingRestore.PathIn(_paths)));
        Assert.Null(PendingRestore.RunIfPending(_paths, protector)); // only once

        // A marker pointing at a missing file is reported and removed, never retried.
        new PendingRestore(Path.Combine(_root, "missing.zip"), "admin", DateTime.UtcNow, null).Save(_paths);
        Assert.StartsWith("FAILED", PendingRestore.RunIfPending(_paths, protector));
        Assert.False(File.Exists(PendingRestore.PathIn(_paths)));
        _sp = new ServiceCollection().BuildServiceProvider();
    }

    [Fact]
    public async Task Damaged_backups_are_rejected_before_restore()
    {
        var backups = _sp.GetRequiredService<BackupService>();
        var info = await backups.CreateAsync("manual", CancellationToken.None);
        var damaged = Path.Combine(_root, "damaged.zip");
        File.Copy(backups.PathOf(info.Name)!, damaged);
        using (var zip = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            zip.GetEntry("appsettings.json")?.Delete();
            using var w = new StreamWriter(zip.CreateEntry("appsettings.json").Open());
            w.Write("{\"tampered\":true}");
        }

        if (File.Exists(_paths.SettingsFile))
        {
            Assert.Throws<InvalidDataException>(() => BackupRestore.Validate(damaged));
        }

        Assert.Throws<InvalidDataException>(() => BackupRestore.Validate(damaged, Password)); // no portable export in this backup
        Assert.Equal(14, new BackupSettings().KeepCount);
        Assert.Equal("02:00", new BackupSettings().DailyAt);
    }
}
