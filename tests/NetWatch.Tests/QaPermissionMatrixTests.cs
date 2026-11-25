using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetWatch.Alerts;
using NetWatch.Collectors;
using NetWatch.Core.Configuration;
using NetWatch.Data;
using NetWatch.Web;
using NetWatch.Web.Auth;

namespace NetWatch.Tests;

/// <summary>
/// Release QA (TEST_PLAN SEC-30): the permission matrix, executed. The real host runs over HTTPS; every API endpoint
/// of the route table is called as anonymous, NOC kiosk, Viewer, Operator and Admin. Viewer and kiosk may not change
/// anything (403), Operator may not administer (403), Admin is never refused, anonymous always gets 401, and no
/// endpoint answers an empty or minimal request with a server error.
/// </summary>
[Collection(nameof(StartupTests))]
public sealed class QaPermissionMatrixTests : IAsyncLifetime
{
    private static readonly HashSet<string> Anonymous = ["POST /api/auth/login", "POST /api/auth/logout", "GET /api/auth/options", "POST /api/auth/kiosk",
        // Endpoint inventory from the GPO script: authenticated by the enrollment key or the machine token, not a session.
        "POST /api/endpoints/report"];

    /// <summary>A Viewer's own password and saved searches; report generation/preview/export are reads sent as POST/GET.</summary>
    private static readonly HashSet<string> ViewerAllowedWrites =
        ["POST /api/auth/change-password", "POST /api/saved-searches/", "DELETE /api/saved-searches/{id:int}", "POST /api/reports/run", "POST /api/reports/preview"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netwatch-matrix-" + Guid.NewGuid().ToString("N"));
    private int _port;
    private Task<int> _host = null!;

    public async Task InitializeAsync()
    {
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        _host = Task.Run(() => NetWatch.Service.Program.Main(["--self-test", "--data-dir", _dir, $"--Https:Port={_port}", "--SelfTest:Seconds=90"]));
        using var http = Client();
        for (var i = 0; i < 160; i++)
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

    private HttpClient Client() => new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    {
        BaseAddress = new Uri($"https://localhost:{_port}"),
        DefaultRequestHeaders = { { "X-NetWatch-Request", "1" } },
        Timeout = TimeSpan.FromSeconds(60),
    };

    private sealed record Endpoint(string Method, string Pattern, bool IsAnonymous, IReadOnlyList<string> Policies)
    {
        public string Key => $"{Method} {Pattern}";
    }

    private static List<Endpoint> RouteTable()
    {
        var root = Path.Combine(Path.GetTempPath(), "netwatch-matrix-routes-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNetWatchData(new NetWatchPaths(root));
        builder.Services.AddNetWatchAlerts();
        builder.Services.AddNetWatchCollectors(false);
        builder.Services.AddNetWatchWeb();
        builder.Services.AddSingleton(new ServerCertificateInfo(null, null, null));
        var app = builder.Build();
        app.UseNetWatchWeb();
        var list = new List<Endpoint>();
        foreach (var e in ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>())
        {
            var pattern = "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            if (!pattern.StartsWith("/api", StringComparison.Ordinal))
            {
                continue;
            }

            var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).OfType<string>().ToList();
            foreach (var m in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                list.Add(new Endpoint(m, pattern, e.Metadata.GetMetadata<IAllowAnonymous>() is not null, policies));
            }
        }

        return list;
    }

    /// <summary>Concrete URL: route values that do not exist (so writes find nothing to change), valid formats.</summary>
    private static string Url(string pattern) =>
        System.Text.RegularExpressions.Regex.Replace(pattern, @"\{([^}:]+)(:[^}]+)?\}", m => m.Groups[1].Value switch
        {
            "id" when m.Groups[2].Value == ":guid" => Guid.NewGuid().ToString(),
            "name" => "netwatch-backup-19990101-000000.zip",
            "month" => "1999-01",
            "file" => "missing.pdf",
            "channel" => "nosuch",
            _ => "999999",
        }).TrimEnd('/') is { Length: > 0 } u ? u : "/";

    private static async Task<HttpStatusCode> CallAsync(HttpClient http, Endpoint e)
    {
        using var request = new HttpRequestMessage(new HttpMethod(e.Method), Url(e.Pattern));
        if (e.Method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    private async Task<HttpClient> SignInAsync(string user, string password)
    {
        var http = Client();
        var r = await http.PostAsJsonAsync("/api/auth/login", new { username = user, password });
        Assert.True(r.IsSuccessStatusCode, $"{user}: {r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return http;
    }

    [Fact]
    public async Task Every_endpoint_enforces_the_role_matrix_at_run_time()
    {
        // Accounts: admin (one-time password changed), operator, viewer, and a NOC kiosk display.
        var initial = File.ReadAllLines(Path.Combine(_dir, "initial-admin-password.txt"))[2].Split(':', 2)[1].Trim();
        using var admin = await SignInAsync("admin", initial);
        const string adminPassword = "Matrix-Pilot-2026!a";
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = initial, newPassword = adminPassword })).StatusCode);
        foreach (var (name, role) in new[] { ("qa-op", "Operator"), ("qa-view", "Viewer") })
        {
            var created = await admin.PostAsJsonAsync("/api/users", new { username = name, displayName = name, role, password = $"Matrix-{role}-2026!x", mustChangePassword = false });
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        }

        using var operatorClient = await SignInAsync("qa-op", "Matrix-Operator-2026!x");
        using var viewer = await SignInAsync("qa-view", "Matrix-Viewer-2026!x");
        var kioskLink = JsonDocument.Parse(await (await admin.PostAsJsonAsync("/api/kiosks", new { name = "Matrix wall" })).Content.ReadAsStringAsync()).RootElement.GetProperty("path").GetString()!;
        using var kiosk = Client();
        var token = kioskLink[(kioskLink.IndexOf("kiosk=", StringComparison.Ordinal) + 6)..];
        Assert.True((await kiosk.PostAsJsonAsync("/api/auth/kiosk", new { token })).IsSuccessStatusCode);
        using var anonymous = Client();

        var endpoints = RouteTable().Where(e => e.Key is not "POST /api/auth/logout").OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        Assert.True(endpoints.Count > 120, $"only {endpoints.Count} endpoints");
        var violations = new List<string>();
        var matrix = new StringBuilder("endpoint;anonymous;kiosk;viewer;operator;admin\n");

        foreach (var e in endpoints)
        {
            var isRead = e.Method == "GET";
            var adminOnly = e.Policies.Contains(Policies.Admin);
            var operatorOnly = e.Policies.Contains(Policies.Operator);
            var viewerMayCall = !adminOnly && !operatorOnly && (isRead || ViewerAllowedWrites.Contains(e.Key) || e.Key.EndsWith("/security-events/export", StringComparison.Ordinal));

            var row = new List<HttpStatusCode>();
            foreach (var (who, http, allowed) in new (string, HttpClient, bool)[]
            {
                ("anonymous", anonymous, e.IsAnonymous),
                ("kiosk", kiosk, e.IsAnonymous || viewerMayCall),
                ("viewer", viewer, e.IsAnonymous || viewerMayCall),
                ("operator", operatorClient, e.IsAnonymous || !adminOnly),
                ("admin", admin, true),
            })
            {
                // Login/kiosk sign-in with an empty body for signed-in clients would only test validation.
                if (e.IsAnonymous && who != "anonymous")
                {
                    row.Add(0);
                    continue;
                }

                var status = await CallAsync(http, e);
                row.Add(status);
                if ((int)status >= 500)
                {
                    violations.Add($"{who,-9} {e.Key} → {(int)status} (server error)");
                }
                else if (allowed && !e.IsAnonymous && status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) // sign-in with an empty body is rightly 400/401
                {
                    violations.Add($"{who,-9} {e.Key} → {(int)status}, expected access");
                }
                else if (!allowed && who == "anonymous" && status != HttpStatusCode.Unauthorized)
                {
                    violations.Add($"{who,-9} {e.Key} → {(int)status}, expected 401");
                }
                else if (!allowed && who != "anonymous" && status != HttpStatusCode.Forbidden)
                {
                    violations.Add($"{who,-9} {e.Key} → {(int)status}, expected 403");
                }
            }

            matrix.AppendLine($"{e.Key};{string.Join(';', row.Select(s => s == 0 ? "-" : ((int)s).ToString(System.Globalization.CultureInfo.InvariantCulture)))}");
        }

        // The executed matrix is kept as release evidence.
        var evidence = Environment.GetEnvironmentVariable("NETWATCH_QA_EVIDENCE");
        if (!string.IsNullOrEmpty(evidence))
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "permission-matrix.csv"), matrix.ToString());
        }

        Assert.True(violations.Count == 0, $"{violations.Count} violation(s):\n" + string.Join("\n", violations));
    }
}
