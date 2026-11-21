using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NetWatch.Tests;

/// <summary>
/// SignalR end to end: two separately signed-in sessions (two browsers) connect to /hubs/live with the JSON hub
/// protocol over WebSocket; a new alert raised by the simulator must reach both (that is what drives the pop-up).
/// </summary>
[Collection(nameof(StartupTests))]
public sealed class LiveUpdatesIntegrationTests : IAsyncLifetime
{
    private const char RecordSeparator = '\u001e';
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netwatch-live-" + Guid.NewGuid().ToString("N"));
    private int _port;
    private Task<int> _host = null!;

    public async Task InitializeAsync()
    {
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        _host = Task.Run(() => NetWatch.Service.Program.Main(["--self-test", "--simulate", "--data-dir", _dir, $"--Https:Port={_port}", "--SelfTest:Seconds=90"]));
        using var http = Session().Http;
        for (var i = 0; i < 120; i++)
        {
            try
            {
                if ((await http.GetAsync("/api/auth/options")).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("NetWatch did not start");
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

    private (HttpClient Http, CookieContainer Cookies) Session()
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = cookies, ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        var http = new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{_port}") };
        http.DefaultRequestHeaders.Add("X-NetWatch-Request", "1");
        return (http, cookies);
    }

    private async Task<ClientWebSocket> ConnectAsync(HttpClient http, CookieContainer cookies)
    {
        var negotiate = JsonDocument.Parse(await (await http.PostAsync("/hubs/live/negotiate?negotiateVersion=1", null)).EnsureSuccessStatusCode().Content.ReadAsStringAsync()).RootElement;
        var token = negotiate.GetProperty("connectionToken").GetString();
        var ws = new ClientWebSocket();
        ws.Options.Cookies = cookies;
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await ws.ConnectAsync(new Uri($"wss://localhost:{_port}/hubs/live?id={Uri.EscapeDataString(token!)}"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}" + RecordSeparator), WebSocketMessageType.Text, true, CancellationToken.None);
        return ws;
    }

    /// <summary>Reads hub messages until an invocation of <paramref name="target"/> arrives (or the timeout).</summary>
    private static async Task<JsonElement?> WaitForAsync(ClientWebSocket ws, string target, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[64 * 1024];
        var pending = new StringBuilder();
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token);
                pending.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                var text = pending.ToString();
                var last = text.LastIndexOf(RecordSeparator);
                if (last < 0)
                {
                    continue;
                }

                pending.Clear().Append(text[(last + 1)..]);
                foreach (var frame in text[..last].Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var m = JsonDocument.Parse(frame).RootElement;
                    if (m.TryGetProperty("type", out var type) && type.GetInt32() == 1 && m.GetProperty("target").GetString() == target)
                    {
                        return m.GetProperty("arguments")[0].Clone();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    [Fact]
    public async Task A_new_alert_reaches_two_browser_sessions()
    {
        var initial = File.ReadAllLines(Path.Combine(_dir, "initial-admin-password.txt"))[2].Split(':', 2)[1].Trim();
        var (admin, adminCookies) = Session();
        Assert.True((await admin.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = initial })).IsSuccessStatusCode);
        Assert.True((await admin.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = initial, newPassword = "Live-Updates-2026!" })).IsSuccessStatusCode);
        Assert.True((await admin.PostAsJsonAsync("/api/users", new { username = "noc1", displayName = "NOC", role = "Viewer", password = "Second-Browser-2026!", mustChangePassword = false })).IsSuccessStatusCode);
        var (viewer, viewerCookies) = Session();
        Assert.True((await viewer.PostAsJsonAsync("/api/auth/login", new { username = "noc1", password = "Second-Browser-2026!" })).IsSuccessStatusCode);

        using var first = await ConnectAsync(admin, adminCookies);
        using var second = await ConnectAsync(viewer, viewerCookies);

        // The simulator raises its first alerts (old/large VM snapshots, device faults) within about a minute.
        var a = WaitForAsync(first, "alertRaised", TimeSpan.FromSeconds(100));
        var b = WaitForAsync(second, "alertRaised", TimeSpan.FromSeconds(100));
        var alertA = await a;
        var alertB = await b;
        Assert.NotNull(alertA);
        Assert.NotNull(alertB);
        Assert.False(string.IsNullOrEmpty(alertA.Value.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrEmpty(alertB.Value.GetProperty("severity").GetString()));

        // Without a session the hub refuses the connection.
        using var anonymous = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri($"https://localhost:{_port}") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/hubs/live/negotiate?negotiateVersion=1", null)).StatusCode);
    }
}
