using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Alerts;
using NetWatch.Alerts.Notifications;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Data;
using NetWatch.Data.Config;

namespace NetWatch.Tests;

/// <summary>
/// Release QA (TEST_PLAN ALR-20…ALR-27): offline notification channels against local test servers — SMTP (a minimal
/// in-process relay with AUTH), syslog forwarding over UDP and TCP, the script action — and the dispatcher's
/// filtering, digest and failure reporting.
/// </summary>
public sealed class QaNotificationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-qa-notify-" + Guid.NewGuid().ToString("N"));
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
        _paths.EnsureCreated();
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

    private static AlertNotification Notification(NotificationKind kind, Severity severity, string title = "Core switch down", string? device = "CORE-SW-01", long id = 42) =>
        new(kind, new AlertDto(id, 1, 7, device, null, severity, kind == NotificationKind.Resolved ? AlertState.Resolved : AlertState.Active, title,
            "No answer to ping for 3 polls", null, true, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, 1, null, null, null, null, null, null, false, 0, null));

    private SettingsService Settings => _sp.GetRequiredService<SettingsService>();

    private Task SaveAsync(NotificationSettings s) => Settings.SaveAsync(NotificationSettings.Key, s, null, CancellationToken.None);

    // ------------------------------------------------------------------ SMTP

    [Fact]
    public async Task Smtp_sends_through_a_relay_with_authentication_and_decrypted_password()
    {
        await using var relay = new FakeSmtpServer();
        var protector = _sp.GetRequiredService<ISecretProtector>();
        var settings = new NotificationSettings
        {
            SmtpHost = "127.0.0.1", SmtpPort = relay.Port, SmtpSecurity = SmtpSecurity.None, SmtpFrom = "netwatch@corp.local", SmtpTo = "noc@corp.local; bad-address; it@corp.local",
            SmtpUsername = "relay-user", SmtpPasswordProtected = Convert.ToBase64String(protector.Protect("Relay-Secret-1!")),
        };

        var n = Notification(NotificationKind.Raised, Severity.Critical);
        await SmtpChannel.SendAsync(NotificationText.Subject(n), NotificationText.Body(n), settings, protector, CancellationToken.None);

        var mail = Assert.Single(relay.Messages);
        Assert.Equal("relay-user", mail.User);
        Assert.Equal("Relay-Secret-1!", mail.Password);
        Assert.Equal(["<noc@corp.local>", "<it@corp.local>"], mail.Recipients); // the invalid address is skipped
        Assert.Contains("Subject: [HIT SIEM] RAISED Critical: Core switch down", mail.Data);
        Assert.Contains("Device:     CORE-SW-01", mail.Data);
        Assert.Contains(" PKT", mail.Data); // local time zone shown
    }

    [Fact]
    public async Task Smtp_configuration_errors_are_clear()
    {
        var protector = _sp.GetRequiredService<ISecretProtector>();
        async Task<string> Error(NotificationSettings s) =>
            (await Assert.ThrowsAsync<InvalidOperationException>(() => SmtpChannel.SendAsync("s", "b", s, protector, CancellationToken.None))).Message;

        Assert.Equal("No SMTP server configured.", await Error(new NotificationSettings()));
        Assert.Equal("No valid recipient address configured.", await Error(new NotificationSettings { SmtpHost = "x", SmtpTo = "not-an-address" }));
        Assert.Equal("The sender address is invalid.", await Error(new NotificationSettings { SmtpHost = "x", SmtpTo = "a@b.c", SmtpFrom = "" }));
        Assert.True(MailSender.ValidRecipients("a@b.c, d@e.f", out var count) && count == 2);
        Assert.False(MailSender.ValidRecipients("a@b.c, nonsense", out _));
        Assert.False(MailSender.ValidAddress("noc"));
        Assert.False(MailSender.ValidAddress("noc@"));
        Assert.True(MailSender.ValidAddress("NOC Team <noc@corp.local>"));
    }

    [Fact]
    public async Task Dispatcher_filters_by_severity_and_sends_a_digest_for_bursts()
    {
        await using var relay = new FakeSmtpServer();
        await SaveAsync(new NotificationSettings
        {
            SmtpEnabled = true, SmtpHost = "127.0.0.1", SmtpPort = relay.Port, SmtpSecurity = SmtpSecurity.None, SmtpFrom = "netwatch@corp.local", SmtpTo = "noc@corp.local",
            SmtpMinSeverity = Severity.Major,
        });
        using var dispatcher = new NotificationDispatcher(Settings, _sp.GetRequiredService<ISecretProtector>(), _paths, NullLogger<NotificationDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        // One Major alert and one Warning (below the minimum): one mail.
        dispatcher.Enqueue(Notification(NotificationKind.Raised, Severity.Major));
        dispatcher.Enqueue(Notification(NotificationKind.Raised, Severity.Warning, "Minor thing"));
        await relay.WaitForAsync(1);
        Assert.Contains("RAISED Major: Core switch down", relay.Messages[0].Data);

        // A burst of six: one digest.
        for (var i = 0; i < 6; i++)
        {
            dispatcher.Enqueue(Notification(NotificationKind.Raised, Severity.Critical, $"Access switch {i} down", id: 100 + i));
        }

        await relay.WaitForAsync(2);
        await Task.Delay(500);
        Assert.Equal(2, relay.Messages.Count);
        Assert.Contains("6 alert notifications (6 raised/escalated, 0 resolved, worst Critical)", relay.Messages[1].Data.Replace("\r\n ", " ", StringComparison.Ordinal)); // unfold the header
        Assert.DoesNotContain("Minor thing", string.Join("\n", relay.Messages.Select(m => m.Data)));

        var status = Assert.Single(dispatcher.Status, s => s.Channel == NotificationDispatcher.Smtp);
        Assert.Equal(2, status.Sent);
        Assert.Equal(0, status.Failed);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_retries_when_the_relay_is_briefly_unavailable()
    {
        // BUG-001 regression: a relay that refuses the first connection must not lose the notification.
        var port = FreeTcpPort();
        await SaveAsync(new NotificationSettings
        {
            SmtpEnabled = true, SmtpHost = "127.0.0.1", SmtpPort = port, SmtpSecurity = SmtpSecurity.None, SmtpFrom = "netwatch@corp.local", SmtpTo = "noc@corp.local",
            SmtpMinSeverity = Severity.Info,
        });
        using var dispatcher = new NotificationDispatcher(Settings, _sp.GetRequiredService<ISecretProtector>(), _paths, NullLogger<NotificationDispatcher>.Instance)
        {
            RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)],
        };
        await dispatcher.StartAsync(CancellationToken.None);
        dispatcher.Enqueue(Notification(NotificationKind.Raised, Severity.Critical));

        // Nothing listens yet: the first attempt fails. Then the relay comes up on the same port.
        await Task.Delay(2600);
        await using var relay = new FakeSmtpServer(port);
        await relay.WaitForAsync(1, TimeSpan.FromSeconds(15));
        var status = Assert.Single(dispatcher.Status, s => s.Channel == NotificationDispatcher.Smtp);
        Assert.Equal(1, status.Sent);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Test_notification_reports_the_error_instead_of_throwing()
    {
        await SaveAsync(new NotificationSettings { SmtpHost = "127.0.0.1", SmtpPort = FreeTcpPort(), SmtpSecurity = SmtpSecurity.None, SmtpFrom = "a@b.c", SmtpTo = "c@d.e" });
        using var dispatcher = new NotificationDispatcher(Settings, _sp.GetRequiredService<ISecretProtector>(), _paths, NullLogger<NotificationDispatcher>.Instance);
        var error = await dispatcher.TestAsync(NotificationDispatcher.Smtp, CancellationToken.None);
        Assert.NotNull(error);
        Assert.Equal("Unknown channel.", await dispatcher.TestAsync("pigeon", CancellationToken.None));
        Assert.Equal("No syslog target host configured.", await dispatcher.TestAsync(NotificationDispatcher.Syslog, CancellationToken.None));
        var failed = Assert.Single(dispatcher.Status, s => s.Channel == NotificationDispatcher.Smtp);
        Assert.Equal(1, failed.Failed);
        Assert.NotNull(failed.LastError);
    }

    [Fact]
    public async Task Smtp_connection_failures_name_the_server_and_the_cause()
    {
        // BUG-007 regression: "No such host is known." alone did not say which name failed.
        var protector = _sp.GetRequiredService<ISecretProtector>();
        var unresolvable = new NotificationSettings { SmtpHost = "no-such-relay.invalid", SmtpSecurity = SmtpSecurity.None, SmtpFrom = "a@b.c", SmtpTo = "c@d.e" };
        var dns = await Assert.ThrowsAsync<IOException>(() => SmtpChannel.SendAsync("s", "b", unresolvable, protector, CancellationToken.None));
        Assert.Contains("'no-such-relay.invalid' cannot be resolved (DNS)", dns.Message);
        Assert.True(NotificationDispatcher.IsTransient(dns));

        var port = FreeTcpPort();
        var refused = await Assert.ThrowsAsync<IOException>(() => SmtpChannel.SendAsync("s", "b", new NotificationSettings { SmtpHost = "127.0.0.1", SmtpPort = port, SmtpSecurity = SmtpSecurity.None, SmtpFrom = "a@b.c", SmtpTo = "c@d.e" }, protector, CancellationToken.None));
        Assert.Contains($"127.0.0.1:{port} refused the connection", refused.Message);
    }

    // ------------------------------------------------------------------ syslog forwarding

    [Fact]
    public async Task Syslog_forward_udp_is_rfc5424_with_escaped_structured_data()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var s = new NotificationSettings { SyslogForwardHost = "127.0.0.1", SyslogForwardPort = port, SyslogForwardProtocol = ForwardProtocol.Udp };

        await SyslogForwardChannel.SendAsync([Notification(NotificationKind.Raised, Severity.Major, "Line1\nLine2", device: "SW \"A\" [x]\\y")], s, CancellationToken.None);
        var received = Encoding.UTF8.GetString((await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);

        Assert.StartsWith("<131>1 ", received); // local0 (16*8) + error (3)
        Assert.Contains("HIT-SIEM - ALERT [netwatch@32473 event=\"RAISED\" id=\"42\" severity=\"Major\" state=\"Active\" device=\"SW \\\"A\\\" [x\\]\\\\y\"]", received);
        Assert.DoesNotContain('\n', received);
        Assert.EndsWith("Line1 Line2: No answer to ping for 3 polls", received);

        // Resolved notifications use severity notice (5).
        await SyslogForwardChannel.SendAsync([Notification(NotificationKind.Resolved, Severity.Critical)], s, CancellationToken.None);
        Assert.StartsWith("<133>1 ", Encoding.UTF8.GetString((await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer));
    }

    [Fact]
    public async Task Syslog_forward_tcp_uses_octet_counting()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ms = new MemoryStream();
            await client.GetStream().CopyToAsync(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        });

        var s = new NotificationSettings { SyslogForwardHost = "127.0.0.1", SyslogForwardPort = port, SyslogForwardProtocol = ForwardProtocol.Tcp };
        await SyslogForwardChannel.SendAsync([Notification(NotificationKind.Raised, Severity.Warning), Notification(NotificationKind.Escalated, Severity.Critical, "Ünïcode ✓")], s, CancellationToken.None);
        var stream = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        // Two frames "<len> <msg>"; the length counts UTF-8 bytes.
        var bytes = Encoding.UTF8.GetBytes(stream);
        var pos = 0;
        var frames = new List<string>();
        while (pos < bytes.Length)
        {
            var space = Array.IndexOf(bytes, (byte)' ', pos);
            var len = int.Parse(Encoding.ASCII.GetString(bytes, pos, space - pos), System.Globalization.CultureInfo.InvariantCulture);
            frames.Add(Encoding.UTF8.GetString(bytes, space + 1, len));
            pos = space + 1 + len;
        }

        Assert.Equal(2, frames.Count);
        Assert.StartsWith("<132>1 ", frames[0]);
        Assert.Contains("event=\"ESCALATED\"", frames[1]);
        Assert.Contains("Ünïcode ✓", frames[1]);
    }

    // ------------------------------------------------------------------ script

    [Fact]
    public async Task Script_receives_alert_data_in_environment_and_stdin_without_command_injection()
    {
        var output = Path.Combine(_paths.ScriptsDir, "out.txt");
        var marker = Path.Combine(_paths.ScriptsDir, "injected.txt");
        File.WriteAllText(Path.Combine(_paths.ScriptsDir, "notify.cmd"),
            "@echo off\r\n>\"%~dp0out.txt\" echo EVENT=%NETWATCH_EVENT% SEV=%NETWATCH_SEVERITY% ID=%NETWATCH_ALERT_ID% DEV=%NETWATCH_DEVICE_ID%\r\nmore >>\"%~dp0out.txt\"\r\n");
        var s = new NotificationSettings { ScriptFile = "notify.cmd", ScriptTimeoutSec = 20 };

        // A title that would run a second command if it were put on a command line.
        await ScriptChannel.RunAsync(Notification(NotificationKind.Raised, Severity.Critical, $"x\" & echo pwned > \"{marker}\" & \""), s, _paths, CancellationToken.None);

        var text = File.ReadAllText(output);
        Assert.Contains("EVENT=RAISED SEV=Critical ID=42 DEV=7", text);
        Assert.Contains("\"event\":\"RAISED\"", text); // JSON on stdin
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Script_failures_and_timeouts_are_reported_and_names_are_validated()
    {
        File.WriteAllText(Path.Combine(_paths.ScriptsDir, "fail.cmd"), "@echo off\r\necho relay unreachable 1>&2\r\nexit /b 3\r\n");
        File.WriteAllText(Path.Combine(_paths.ScriptsDir, "hang.cmd"), "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n");
        var n = Notification(NotificationKind.Raised, Severity.Major);

        var fail = await Assert.ThrowsAsync<InvalidOperationException>(() => ScriptChannel.RunAsync(n, new NotificationSettings { ScriptFile = "fail.cmd", ScriptTimeoutSec = 20 }, _paths, CancellationToken.None));
        Assert.Equal("Script exited with code 3: relay unreachable", fail.Message);

        var started = DateTime.UtcNow;
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => ScriptChannel.RunAsync(n, new NotificationSettings { ScriptFile = "hang.cmd", ScriptTimeoutSec = 2 }, _paths, CancellationToken.None));
        Assert.Contains("did not finish within 2 s", timeout.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));

        foreach (var bad in new[] { "..\\fail.cmd", "sub\\fail.cmd", "C:\\Windows\\System32\\calc.exe", "notes.txt", "", "fail.cmd & calc" })
        {
            Assert.Throws<InvalidOperationException>(() => NotificationDispatcher.ValidateScript(_paths, bad));
        }

        Assert.Contains("Script not found", Assert.Throws<InvalidOperationException>(() => NotificationDispatcher.ValidateScript(_paths, "missing.ps1")).Message);
    }

    // ------------------------------------------------------------------ helpers

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public sealed record ReceivedMail(string? User, string? Password, List<string> Recipients, string Data);

    /// <summary>A minimal SMTP relay (EHLO, AUTH PLAIN/LOGIN, MAIL, RCPT, DATA, QUIT) for tests; no TLS.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeSmtpServer(int port = 0)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public List<ReceivedMail> Messages { get; } = [];

        public async Task WaitForAsync(int count, TimeSpan? timeout = null)
        {
            var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
            while (DateTime.UtcNow < until)
            {
                lock (Messages)
                {
                    if (Messages.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"expected {count} mail(s), got {Messages.Count}");
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            string? user = null, password = null;
            var rcpt = new List<string>();
            await writer.WriteLineAsync("220 fake ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250-fake");
                    await writer.WriteLineAsync("250 AUTH PLAIN LOGIN");
                }
                else if (cmd.StartsWith("AUTH PLAIN", StringComparison.Ordinal))
                {
                    var payload = line.Length > 11 ? line[11..] : null;
                    if (payload is null)
                    {
                        await writer.WriteLineAsync("334 ");
                        payload = await reader.ReadLineAsync();
                    }

                    var parts = Encoding.UTF8.GetString(Convert.FromBase64String(payload!)).Split('\0');
                    (user, password) = (parts[1], parts[2]);
                    await writer.WriteLineAsync("235 ok");
                }
                else if (cmd.StartsWith("AUTH LOGIN", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                    user = Encoding.UTF8.GetString(Convert.FromBase64String((await reader.ReadLineAsync())!));
                    await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                    password = Encoding.UTF8.GetString(Convert.FromBase64String((await reader.ReadLineAsync())!));
                    await writer.WriteLineAsync("235 ok");
                }
                else if (cmd.StartsWith("MAIL FROM", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250 ok");
                }
                else if (cmd.StartsWith("RCPT TO", StringComparison.Ordinal))
                {
                    rcpt.Add(line[(line.IndexOf(':') + 1)..].Split(' ')[0].Trim());
                    await writer.WriteLineAsync("250 ok");
                }
                else if (cmd == "DATA")
                {
                    await writer.WriteLineAsync("354 go");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } d && d != ".")
                    {
                        data.AppendLine(d);
                    }

                    lock (Messages)
                    {
                        Messages.Add(new ReceivedMail(user, password, [.. rcpt], data.ToString()));
                    }

                    rcpt.Clear();
                    await writer.WriteLineAsync("250 queued");
                }
                else if (cmd == "QUIT")
                {
                    await writer.WriteLineAsync("221 bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("250 ok");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
            }
        }
    }
}
