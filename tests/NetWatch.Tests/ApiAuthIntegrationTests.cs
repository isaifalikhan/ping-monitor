using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NetWatch.Tests;

/// <summary>
/// API integration over real HTTPS: the actual host runs (simulator, empty data folder) and the tests sign in
/// as admin and as a Viewer to check authentication, the CSRF header, forced password change and role permissions.
/// </summary>
[Collection(nameof(StartupTests))]
public sealed class ApiAuthIntegrationTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netwatch-api-" + Guid.NewGuid().ToString("N"));
    private int _port;
    private Task<int> _host = null!;

    public async Task InitializeAsync()
    {
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        _host = Task.Run(() => NetWatch.Service.Program.Main(["--self-test", "--simulate", "--data-dir", _dir, $"--Https:Port={_port}", "--SelfTest:Seconds=15"]));
        using var http = Client();
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
        await _host; // the self-test stops by itself
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private HttpClient Client(bool csrf = true)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // the test host's self-signed certificate
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{_port}") };
        if (csrf)
        {
            http.DefaultRequestHeaders.Add("X-NetWatch-Request", "1");
        }

        return http;
    }

    private string InitialPassword() => File.ReadAllLines(Path.Combine(_dir, "initial-admin-password.txt"))[2].Split(':', 2)[1].Trim();

    private static async Task<HttpStatusCode> LoginAsync(HttpClient http, string user, string password) =>
        (await http.PostAsJsonAsync("/api/auth/login", new { username = user, password })).StatusCode;

    [Fact]
    public async Task Authentication_csrf_and_roles_are_enforced()
    {
        // No session: 401.
        using var anonymous = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/devices")).StatusCode);

        // State-changing call without the CSRF header: refused before anything else.
        using var noCsrf = Client(csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await noCsrf.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "x" })).StatusCode);

        // Wrong password: 401; one-time password: signed in but forced to change it first.
        using var admin = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(admin, "admin", "wrong-password-1"));
        var initial = InitialPassword();
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(admin, "admin", initial));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/devices")).StatusCode);
        const string adminPassword = "Integration-Pilot-2026!";
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = initial, newPassword = adminPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/devices")).StatusCode);

        // A Viewer account.
        const string viewerPassword = "Integration-Reader-2026!";
        var created = await admin.PostAsJsonAsync("/api/users", new { username = "viewer1", displayName = "Viewer One", role = "Viewer", password = viewerPassword, mustChangePassword = false });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        using var viewer = Client();
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(viewer, "viewer1", viewerPassword));
        var me = JsonDocument.Parse(await viewer.GetStringAsync("/api/auth/me")).RootElement;
        Assert.Equal("Viewer", me.GetProperty("role").GetString());

        // Viewer can read ...
        foreach (var path in new[] { "/api/devices", "/api/alerts", "/api/overview", "/api/dc/summary", "/api/vmware/hosts", "/api/topology/links" })
        {
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(path)).StatusCode);
        }

        // ... but not change anything or open administration.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/devices", new { name = "X", ipAddress = "10.9.9.9", type = "Switch", os = "NetworkOs", isEnabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/alerts/ack", new { ids = new[] { 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/credentials")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/backups", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/diagnostics/bundle")).StatusCode);

        // Signing out ends the session.
        Assert.True((await viewer.PostAsync("/api/auth/logout", null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/devices")).StatusCode);

        // Security headers on every response.
        var response = await admin.GetAsync("/api/overview");
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.True(response.Headers.Contains("X-Frame-Options"));
    }
}
