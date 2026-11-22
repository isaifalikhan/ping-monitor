using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Data;
using NetWatch.Data.Config;

namespace NetWatch.Tests;

/// <summary>
/// Release QA (TEST_PLAN SW-30…SW-33): the syslog (UDP + TCP) and SNMP trap (v1 + v2c) receivers over real sockets
/// of the running host. Messages are sent from 127.0.0.1 (the NetWatch self-monitoring device) and must be parsed,
/// stored, mapped to that device and found by search, with the right severity.
/// </summary>
[Collection(nameof(StartupTests))]
public sealed class QaReceiverIntegrationTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netwatch-receivers-" + Guid.NewGuid().ToString("N"));
    private int _https, _udp, _tcp, _trap;
    private Task<int> _host = null!;

    private static int FreePort(bool udp)
    {
        if (udp)
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
        }

        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async Task InitializeAsync()
    {
        (_https, _tcp, _udp, _trap) = (FreePort(false), FreePort(false), FreePort(true), FreePort(true));

        // Pre-create the database with receiver ports that are free on this machine (162/514 may be taken).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(new NetWatchPaths(_dir));
        await using (var sp = services.BuildServiceProvider())
        {
            await sp.InitialiseDatabasesAsync(NullLogger.Instance);
            await sp.GetRequiredService<SettingsService>().SaveAsync(ReceiverSettings.Key,
                new ReceiverSettings { SyslogUdpPort = _udp, SyslogTcpPort = _tcp, TrapPort = _trap }, null, CancellationToken.None);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _host = Task.Run(() => NetWatch.Service.Program.Main(["--self-test", "--data-dir", _dir, $"--Https:Port={_https}", "--SelfTest:Seconds=45"]));
        using var http = Client();
        for (var i = 0; i < 160; i++)
        {
            try
            {
                if ((await http.GetAsync("/api/auth/options")).IsSuccessStatusCode)
                {
                    await Task.Delay(1500); // receivers start with the host
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(250);
        }

        throw new System.TimeoutException("NetWatch did not start");
    }

    public async Task DisposeAsync()
    {
        await _host;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
    {
        BaseAddress = new Uri($"https://localhost:{_https}"),
        DefaultRequestHeaders = { { "X-NetWatch-Request", "1" } },
    };

    private static async Task<JsonElement> WaitForAsync(HttpClient http, string url, Func<JsonElement, bool> ready)
    {
        JsonElement last = default;
        for (var i = 0; i < 60; i++)
        {
            last = JsonDocument.Parse(await http.GetStringAsync(url)).RootElement;
            if (ready(last))
            {
                return last;
            }

            await Task.Delay(250);
        }

        return last;
    }

    [Fact]
    public async Task Syslog_and_traps_arrive_over_the_network_and_are_stored_mapped_and_searchable()
    {
        using var http = Client();
        var initial = File.ReadAllLines(Path.Combine(_dir, "initial-admin-password.txt"))[2].Split(':', 2)[1].Trim();
        Assert.True((await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = initial })).IsSuccessStatusCode);
        Assert.True((await http.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = initial, newPassword = "Receiver-Test-2026!" })).IsSuccessStatusCode);
        // The sender address 127.0.0.1 is a known device (the self-monitoring device, or one added here).
        await http.PostAsJsonAsync("/api/devices", new { name = "QA-LOOPBACK", ipAddress = "127.0.0.1", type = "Switch", os = "NetworkOs", isEnabled = true });
        await Task.Delay(3000); // inventory cache refresh
        var self = JsonDocument.Parse(await http.GetStringAsync("/api/devices")).RootElement.EnumerateArray().First(d => d.GetProperty("ipAddress").GetString() == "127.0.0.1").GetProperty("id").GetInt32();

        var tag = Guid.NewGuid().ToString("N")[..8];

        // Syslog over UDP: RFC 3164 (Cisco style, severity 2 = critical) and RFC 5424 (severity 4 = warning).
        using (var udp = new UdpClient())
        {
            foreach (var line in new[]
            {
                $"<186>1234: Nov 22 06:00:00: %PLATFORM_ENV-2-FAN: QA-{tag} fan 1 failed",
                $"<12>1 2026-11-22T01:00:00.000Z sw-qa app 42 ID47 - QA-{tag} rfc5424 warning",
            })
            {
                var b = Encoding.UTF8.GetBytes(line);
                await udp.SendAsync(b, b.Length, new IPEndPoint(IPAddress.Loopback, _udp));
            }
        }

        // Syslog over TCP: octet counting and newline framing in one connection.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, _tcp);
            var s = tcp.GetStream();
            var framed = $"<14>Nov 22 06:00:01 sw-qa ports: QA-{tag} tcp octet";
            await s.WriteAsync(Encoding.UTF8.GetBytes($"{Encoding.UTF8.GetByteCount(framed)} {framed}"));
            await s.WriteAsync(Encoding.UTF8.GetBytes($"<11>Nov 22 06:00:02 sw-qa ports: QA-{tag} tcp newline\n"));
            await s.FlushAsync();
            await Task.Delay(300);
        }

        var syslog = await WaitForAsync(http, $"/api/syslog?search=QA-{tag}&pageSize=50", r => r.GetProperty("total").GetInt32() >= 4);
        var rows = syslog.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(self, r.GetProperty("deviceId").GetInt32()));
        int Sev(string text) => rows.Single(r => r.GetProperty("message").GetString()!.Contains(text, StringComparison.Ordinal)).GetProperty("severity").GetInt32();
        Assert.Equal(2, Sev("fan 1 failed"));
        Assert.Equal(4, Sev("rfc5424 warning"));
        Assert.Equal(6, Sev("tcp octet"));
        Assert.Equal(3, Sev("tcp newline"));

        // Severity filter: critical and worse only.
        var critical = JsonDocument.Parse(await http.GetStringAsync($"/api/syslog?search=QA-{tag}&maxSeverity=2")).RootElement;
        Assert.Equal(1, critical.GetProperty("total").GetInt32());

        // SNMP traps: v2c linkDown and linkUp with ifIndex, v1 coldStart and authenticationFailure.
        var receiver = new IPEndPoint(IPAddress.Loopback, _trap);
        var community = new OctetString("public");
        Variable IfIndex(int i) => new(new ObjectIdentifier($"1.3.6.1.2.1.2.2.1.1.{i}"), new Integer32(i));
        Messenger.SendTrapV2(1, VersionCode.V2, receiver, community, new ObjectIdentifier("1.3.6.1.6.3.1.1.5.3"), 1000, [IfIndex(17)]);
        Messenger.SendTrapV2(2, VersionCode.V2, receiver, community, new ObjectIdentifier("1.3.6.1.6.3.1.1.5.4"), 1001, [IfIndex(17)]);
        Messenger.SendTrapV1(receiver, IPAddress.Loopback, community, new ObjectIdentifier("1.3.6.1.4.1.9"), GenericCode.ColdStart, 0, 5, []);
        Messenger.SendTrapV1(receiver, IPAddress.Loopback, community, new ObjectIdentifier("1.3.6.1.4.1.9"), GenericCode.AuthenticationFailure, 0, 6, []);

        var traps = await WaitForAsync(http, $"/api/traps?deviceId={self}&pageSize=50", r => r.GetProperty("total").GetInt32() >= 4);
        var names = traps.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("trapName").GetString()).ToList();
        Assert.Contains("linkDown", names);
        Assert.Contains("linkUp", names);
        Assert.Contains("coldStart", names);
        Assert.Contains("authenticationFailure", names);
        var linkDown = traps.GetProperty("items").EnumerateArray().First(t => t.GetProperty("trapName").GetString() == "linkDown");
        Assert.Contains("ifIndex 17", linkDown.GetProperty("summary").GetString());

        // Receiver statistics on the status page.
        var status = JsonDocument.Parse(await http.GetStringAsync("/api/system/status")).RootElement.GetProperty("receivers");
        Assert.True(status.GetProperty("syslogReceived").GetInt64() >= 4);
        Assert.True(status.GetProperty("trapsReceived").GetInt64() >= 4);
        Assert.Equal(0, status.GetProperty("parseErrors").GetInt64());
    }
}
