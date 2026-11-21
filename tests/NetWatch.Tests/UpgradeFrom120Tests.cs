using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Credentials;
using NetWatch.Data;
using NetWatch.Data.Config;

namespace NetWatch.Tests;

/// <summary>
/// Upgrade path: a configuration database at the 1.2.0 schema (last migration Phase3) with devices, credentials,
/// settings and alerts is upgraded by the normal start-up code to 2.0, and nothing is lost.
/// </summary>
[Collection(nameof(SqliteFileMoves))]
public sealed class UpgradeFrom120Tests : IDisposable
{
    private const string Phase3 = "20261120173913_Phase3";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-upgrade-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_120_database_upgrades_with_its_data()
    {
        var paths = new NetWatchPaths(_root);
        paths.EnsureCreated();
        var protector = new Data.Security.DpapiSecretProtector();

        // 1. Build the 1.2.0 schema.
        var options = new DbContextOptionsBuilder<NetWatchDbContext>().UseSqlite(SqliteConnectionFactory.Build(paths.ConfigDb)).Options;
        using (var old = new NetWatchDbContext(options))
        {
            old.GetService<IMigrator>().Migrate(Phase3);
        }

        // 2. Data as 1.2.0 wrote it (raw SQL: the 2.0 model has more columns than this schema).
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder(SqliteConnectionFactory.Build(paths.ConfigDb)) { Pooling = false }.ToString()))
        {
            c.Open();
            Insert(c, "CredentialProfiles", new() { ["Name"] = "Core SNMP", ["Kind"] = 0, ["DisplayHint"] = "community: ********",
                ["SecretBlob"] = protector.Protect(new CredentialSecret { Community = "kept-community" }.ToJson()) });
            Insert(c, "Devices", new() { ["Name"] = "CORE-SW-01", ["IpAddress"] = "10.0.0.1", ["Type"] = 0, ["PollIntervalSec"] = 60, ["IsEnabled"] = 1 });
            Insert(c, "Devices", new() { ["Name"] = "SRV-APP-01", ["IpAddress"] = "10.0.10.5", ["Type"] = 3, ["PollIntervalSec"] = 60, ["IsEnabled"] = 1 });
            Insert(c, "Settings", new() { ["Key"] = "security", ["ValueJson"] = "{\"sessionIdleTimeoutMin\":45}" });
            Insert(c, "Users", new() { ["Username"] = "operator1", ["DisplayName"] = "Operator", ["Role"] = 1, ["IsEnabled"] = 1, ["AuthSource"] = 0 });
        }

        // 3. Start-up of 2.0 on that data folder.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(paths);
        await using (var sp = services.BuildServiceProvider())
        {
            await sp.InitialiseDatabasesAsync(NullLogger.Instance);
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(["CORE-SW-01", "SRV-APP-01"], await db.Devices.OrderBy(d => d.Name).Select(d => d.Name).ToListAsync());
            var profile = await db.CredentialProfiles.SingleAsync();
            Assert.Equal("kept-community", CredentialSecret.FromJson(scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(profile.SecretBlob)).Community);
            Assert.Equal(45, scope.ServiceProvider.GetRequiredService<SettingsService>().Security.SessionIdleTimeoutMin);
            Assert.Contains(await db.Users.Select(u => u.Username).ToListAsync(), u => u == "operator1");

            // New 2.0 columns and tables exist and are usable.
            var sw = await db.Devices.SingleAsync(d => d.Name == "CORE-SW-01");
            Assert.Null(sw.BmcAddress);
            Assert.Equal(0, await db.NeighborLinks.CountAsync());
            Assert.Equal(0, await db.KioskDisplays.CountAsync());
            Assert.Equal(0, await db.ServerGpus.CountAsync());
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>INSERT with the given values; other NOT NULL columns without a default get a neutral value.</summary>
    private static void Insert(SqliteConnection c, string table, Dictionary<string, object> values)
    {
        using (var info = c.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var r = info.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(1);
                var type = r.GetString(2).ToUpperInvariant();
                var notNull = r.GetInt32(3) == 1;
                var hasDefault = !r.IsDBNull(4);
                var pk = r.GetInt32(5) == 1;
                if (pk || !notNull || hasDefault || values.ContainsKey(name))
                {
                    continue;
                }

                values[name] = type.Contains("INT", StringComparison.Ordinal) ? 0
                    : type.Contains("BLOB", StringComparison.Ordinal) ? Array.Empty<byte>()
                    : type.Contains("REAL", StringComparison.Ordinal) ? 0.0
                    : name.EndsWith("Utc", StringComparison.Ordinal) ? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF") : string.Empty;
            }
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", values.Keys.Select(k => $"\"{k}\""))}) VALUES ({string.Join(", ", values.Keys.Select((_, i) => $"$p{i}"))})";
        var j = 0;
        foreach (var v in values.Values)
        {
            cmd.Parameters.AddWithValue($"$p{j++}", v);
        }

        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
