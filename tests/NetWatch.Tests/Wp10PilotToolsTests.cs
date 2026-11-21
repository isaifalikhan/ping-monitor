using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Lextm.SharpSnmpLib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Collectors.Scheduling;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Credentials;
using NetWatch.Core.Entities;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Diagnostics;

namespace NetWatch.Tests;

/// <summary>WP10: connection tests and the diagnostic bundle (no secret may ever leave in it).</summary>
public sealed class Wp10PilotToolsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-wp10-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(new NetWatchPaths(_root));
        _sp = services.BuildServiceProvider();
        await _sp.InitialiseDatabasesAsync(NullLogger.Instance);
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ConnectionTestService Tests() => new(_sp.GetRequiredService<IServiceScopeFactory>(), null!, null!, null!, null!, null!, _sp.GetRequiredService<SettingsService>(),
        _sp.GetRequiredService<ISecretProtector>());

    [Fact]
    public void Redacts_every_secret_property_at_any_depth()
    {
        var node = JsonNode.Parse("""
            { "smtpHost": "10.0.0.25", "smtpPasswordProtected": "AQAAANCMnd8B...", "https": { "port": 8443, "certificatePassword": "pfx!" },
              "ldap": { "servers": ["dc01"], "bindPassword": "x" }, "list": [ { "community": "public" }, { "authKey": "k", "name": "keep" } ], "keepCount": 14, "empty": { "password": "" } }
            """);
        var text = DiagnosticBundleService.Redact(node)!.ToJsonString();
        Assert.DoesNotContain("AQAAANCMnd8B", text);
        Assert.DoesNotContain("pfx!", text);
        Assert.DoesNotContain("\"public\"", text);
        Assert.DoesNotContain("\"k\"", text);
        Assert.Contains("10.0.0.25", text);
        Assert.Contains("\"keepCount\":14", text);
        Assert.Contains("\"name\":\"keep\"", text);
        Assert.Contains("8443", text);
    }

    [Fact]
    public void Walk_lines_use_the_snmpwalk_format_the_parser_tests_read()
    {
        Assert.Equal(".1.3.6.1.2.1.1.5.0 = STRING: \"CORE-01\"", DiagnosticBundleService.Format("1.3.6.1.2.1.1.5", "0", new OctetString("CORE-01")));
        Assert.Equal(".1.0.8802.1.1.2.1.4.1.1.5.0.57.1 = Hex-STRING: 70 10 6F 2A 11 C0",
            DiagnosticBundleService.Format("1.0.8802.1.1.2.1.4.1.1.5", "0.57.1", new OctetString([0x70, 0x10, 0x6F, 0x2A, 0x11, 0xC0])));
        Assert.Equal(".1.3.6.1.2.1.10.7.2.1.19.10 = INTEGER: 2", DiagnosticBundleService.Format("1.3.6.1.2.1.10.7.2.1.19", "10", new Integer32(2)));
        Assert.Equal(".1.3.6.1.2.1.1.3.0 = Timeticks: (12345)", DiagnosticBundleService.Format("1.3.6.1.2.1.1.3", "0", new TimeTicks(12345)));
    }

    [Fact]
    public async Task Ping_test_reports_each_step_and_is_kept_in_the_history()
    {
        var tests = Tests();
        var r = await tests.RunAsync(new ConnectionTestRequest(ConnectionTestKind.Ping, null, "127.0.0.1", null), "admin", CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("ms average", Assert.Single(r.Steps).Detail);
        Assert.Single(tests.History);

        var missing = await tests.RunAsync(new ConnectionTestRequest(ConnectionTestKind.Snmp, null, "127.0.0.1", null), "admin", CancellationToken.None);
        Assert.False(missing.Ok);
        Assert.Contains("SnmpV2c or SnmpV3 credential", missing.Error);

        var noTarget = await tests.RunAsync(new ConnectionTestRequest(ConnectionTestKind.Ping, null, null, null), "admin", CancellationToken.None);
        Assert.Equal("Choose a device or enter an address.", noTarget.Error);
    }

    [Fact]
    public async Task Receivers_test_counts_recent_syslog_and_traps_from_the_source()
    {
        using (var scope = _sp.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
            events.SyslogMessages.Add(new SyslogMessage { ReceivedUtc = DateTime.UtcNow.AddMinutes(-2), SourceIp = "10.1.1.1", Message = "link up" });
            events.SyslogMessages.Add(new SyslogMessage { ReceivedUtc = DateTime.UtcNow.AddHours(-3), SourceIp = "10.1.1.1", Message = "old" });
            await events.SaveChangesAsync();
        }

        var r = await Tests().RunAsync(new ConnectionTestRequest(ConnectionTestKind.Receivers, null, "10.1.1.1", null, Minutes: 15), "admin", CancellationToken.None);
        Assert.Equal("1 syslog, 0 traps", r.Summary);
        Assert.True(r.Steps[0].Ok);
        Assert.False(r.Steps[1].Ok);
        Assert.Contains("UDP 162", r.Steps[1].Detail);
    }

    [Fact]
    public async Task Bundle_contains_logs_config_inventory_and_no_secret()
    {
        var paths = _sp.GetRequiredService<NetWatchPaths>();
        Directory.CreateDirectory(paths.LogsDir);
        await File.WriteAllTextAsync(Path.Combine(paths.LogsDir, "netwatch-20261121.log"), "2026-11-21 [INF] started\n");
        await File.WriteAllTextAsync(paths.SettingsFile, "{ \"Https\": { \"Port\": 8443, \"CertificatePassword\": \"pfx-secret\" } }");
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            var now = DateTime.UtcNow;
            db.CredentialProfiles.Add(new CredentialProfile { Name = "Core", Kind = CredentialKind.SnmpV2c, DisplayHint = "community: ********", CreatedUtc = now, UpdatedUtc = now,
                SecretBlob = protector.Protect(new CredentialSecret { Community = "s3cr3t-community" }.ToJson()) });
            db.Settings.Add(new SettingEntry { Key = "notifications", ValueJson = "{\"smtpHost\":\"10.0.0.25\",\"smtpPasswordProtected\":\"QUFBQQ==\"}", UpdatedUtc = now });
            db.Devices.Add(new Device { Name = "SW-01", IpAddress = "10.0.0.1", Type = DeviceType.Switch, CreatedUtc = now, UpdatedUtc = now, StatusSinceUtc = now });
            await db.SaveChangesAsync();
        }

        var bundle = new DiagnosticBundleService(_sp.GetRequiredService<IServiceScopeFactory>(), paths, Tests(), new CollectorStats(), _sp.GetRequiredService<ISecretProtector>(),
            _sp.GetRequiredService<SettingsService>());
        var bytes = await bundle.BuildAsync([], CancellationToken.None);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("logs/netwatch-20261121.log", names);
        Assert.Contains("config/settings.json", names);
        Assert.Contains("config/appsettings.json", names);
        Assert.Contains("inventory.json", names);
        Assert.Contains("poll-statistics.json", names);
        Assert.Contains("connection-tests.json", names);

        var all = new StringBuilder();
        foreach (var e in zip.Entries)
        {
            using var r = new StreamReader(e.Open());
            all.Append(await r.ReadToEndAsync());
        }

        var text = all.ToString();
        Assert.DoesNotContain("s3cr3t-community", text);
        Assert.DoesNotContain("pfx-secret", text);
        Assert.DoesNotContain("QUFBQQ==", text);
        Assert.Contains("SW-01", text);
        Assert.Contains("10.0.0.25", text);
    }
}
