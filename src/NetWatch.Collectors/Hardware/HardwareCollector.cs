using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Snmp;

namespace NetWatch.Collectors.Hardware;

/// <summary>What to read from a server's management controller in this poll.</summary>
/// <param name="Redfish">iLO/iDRAC Redfish account, or null to use the SNMP fallback.</param>
/// <param name="Snmp">SNMP credential for the fallback (the BMC's or the host OS agent's).</param>
public sealed record HardwarePollRequest(string BmcAddress, ManagementCredential? Redfish, SnmpCredential? Snmp, string? PinnedThumbprint, bool TrustOnFirstUse, TimeSpan Timeout);

public interface IHardwareCollector
{
    Task<HardwarePollData> CollectAsync(HardwarePollRequest request, CancellationToken ct);
}

/// <summary>
/// Reads server hardware health from the management controller: Redfish (HTTPS, basic authentication, read-only
/// account; ComputerSystem, Chassis Thermal/Power, Storage and Drives), or the HPE/Dell SNMP agent as fallback.
/// TLS certificates are pinned like vCenter's (CA-trusted accepted; otherwise SHA-256 pinned on first use).
/// </summary>
public sealed class HardwareCollector : IHardwareCollector
{
    private const int MaxDrives = 48;
    private readonly ILogger<HardwareCollector> _logger;

    public HardwareCollector(ILogger<HardwareCollector> logger)
    {
        _logger = logger;
    }

    public async Task<HardwarePollData> CollectAsync(HardwarePollRequest request, CancellationToken ct)
    {
        if (request.Redfish is not null)
        {
            return await RedfishAsync(request, ct);
        }

        if (request.Snmp is not null)
        {
            return await SnmpAsync(request, ct);
        }

        return new HardwarePollData { Error = "No Redfish or SNMP credential for the management controller." };
    }

    private async Task<HardwarePollData> RedfishAsync(HardwarePollRequest request, CancellationToken ct)
    {
        var data = new HardwarePollData { Source = "Redfish" };
        using var client = new RedfishClient(request.BmcAddress, request.Redfish!.Secret.Username ?? string.Empty, request.Redfish.Secret.Password ?? string.Empty,
            request.PinnedThumbprint, request.TrustOnFirstUse, request.Timeout);
        try
        {
            using var root = await client.GetAsync("/redfish/v1/", ct);
            var systemsLink = RedfishParser.Link(root.RootElement, "Systems") ?? "/redfish/v1/Systems";
            var chassisLink = RedfishParser.Link(root.RootElement, "Chassis") ?? "/redfish/v1/Chassis";

            using var systems = await client.GetAsync(systemsLink, ct);
            foreach (var systemLink in RedfishParser.Links(systems.RootElement).Take(2))
            {
                using var system = await client.GetAsync(systemLink, ct);
                data.Components.AddRange(RedfishParser.System(system.RootElement));
                if (RedfishParser.Link(system.RootElement, "Storage") is { } storageLink)
                {
                    await Part(async () =>
                    {
                        using var storages = await client.GetAsync(storageLink, ct);
                        foreach (var link in RedfishParser.Links(storages.RootElement).Take(8))
                        {
                            using var storage = await client.GetAsync(link, ct);
                            var drives = new List<JsonDocument>();
                            try
                            {
                                foreach (var d in RedfishParser.Links(storage.RootElement, "Drives").Take(MaxDrives))
                                {
                                    drives.Add(await client.GetAsync(d, ct));
                                }

                                data.Components.AddRange(RedfishParser.Storage(storage.RootElement, drives.Select(x => x.RootElement)));
                            }
                            finally
                            {
                                drives.ForEach(x => x.Dispose());
                            }
                        }
                    }, "storage");
                }
            }

            using var chassisList = await client.GetAsync(chassisLink, ct);
            foreach (var link in RedfishParser.Links(chassisList.RootElement).Take(4))
            {
                using var chassis = await client.GetAsync(link, ct);
                if (RedfishParser.Link(chassis.RootElement, "Thermal") is { } thermalLink)
                {
                    await Part(async () =>
                    {
                        using var thermal = await client.GetAsync(thermalLink, ct);
                        data.Components.AddRange(RedfishParser.Thermal(thermal.RootElement));
                    }, "thermal");
                }

                if (RedfishParser.Link(chassis.RootElement, "Power") is { } powerLink)
                {
                    await Part(async () =>
                    {
                        using var power = await client.GetAsync(powerLink, ct);
                        var (readings, watts) = RedfishParser.Power(power.RootElement);
                        data.Components.AddRange(readings);
                        data.PowerWatts ??= watts;
                    }, "power");
                }
            }

            data.Ok = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or RedfishException or TimeoutException && !ct.IsCancellationRequested)
        {
            data.Error = ex is TaskCanceledException ? "Redfish request timed out" : ex.Message;
            _logger.LogDebug(ex, "Redfish read failed for {Host}", request.BmcAddress);
        }

        data.NewTlsThumbprint = client.NewlyPinnedThumbprint;
        data.Components.RemoveAll(c => c.Name.Length == 0);
        return data;

        async Task Part(Func<Task> action, string name)
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or RedfishException)
            {
                _logger.LogDebug(ex, "Redfish {Part} failed for {Host}", name, request.BmcAddress);
            }
        }
    }

    private static async Task<HardwarePollData> SnmpAsync(HardwarePollRequest request, CancellationToken ct)
    {
        var data = new HardwarePollData { Source = "SNMP" };
        try
        {
            if (!IPAddress.TryParse(request.BmcAddress, out var ip))
            {
                ip = (await Dns.GetHostAddressesAsync(request.BmcAddress, ct)).First();
            }

            var client = new SnmpClient(ip, request.Snmp!, (int)Math.Min(request.Timeout.TotalMilliseconds, 10_000), 1);
            var values = await client.GetAsync([HardwareSnmp.HpeCondition, HardwareSnmp.DellGlobalStatus], ct);
            if (HardwareSnmp.HpeHealth(SnmpValues.Int64(values.GetValueOrDefault(HardwareSnmp.HpeCondition))) is { } hpe)
            {
                data.Components.Add(new HardwareReading("System", "Overall health (HPE agent)", hpe));
            }

            if (HardwareSnmp.DellHealth(SnmpValues.Int64(values.GetValueOrDefault(HardwareSnmp.DellGlobalStatus))) is { } dell)
            {
                data.Components.Add(new HardwareReading("System", "Overall health (iDRAC)", dell));
            }

            data.Ok = data.Components.Count > 0;
            data.Error = data.Ok ? null : "Neither the HPE (CPQHLTH) nor the Dell (iDRAC) health OID answered.";
        }
        catch (Exception ex) when (ex is SnmpException or TimeoutException or System.Net.Sockets.SocketException)
        {
            data.Error = ex.Message;
        }

        return data;
    }
}

public sealed class RedfishException(string message) : Exception(message);

/// <summary>Minimal Redfish GET client (basic authentication, certificate pinning, no proxy).</summary>
public sealed class RedfishClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _pinned;
    private readonly bool _trustOnFirstUse;

    public RedfishClient(string host, string user, string password, string? pinnedThumbprint, bool trustOnFirstUse, TimeSpan timeout)
    {
        _pinned = pinnedThumbprint;
        _trustOnFirstUse = trustOnFirstUse;
        var target = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = Validate,

                // Air-gapped: never try to reach a CRL/OCSP responder on the internet.
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://{target}"), Timeout = timeout };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("OData-Version", "4.0");
    }

    public string? SeenThumbprint { get; private set; }

    public string? NewlyPinnedThumbprint { get; private set; }

    public async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RedfishException($"Redfish login refused ({(int)response.StatusCode}): check the account and that it has at least the read-only role.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new RedfishException($"Redfish {path} returned {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 64 }, ct);
    }

    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return false;
        }

        SeenThumbprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        if (_pinned is not null)
        {
            return string.Equals(_pinned, SeenThumbprint, StringComparison.OrdinalIgnoreCase);
        }

        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (_trustOnFirstUse)
        {
            NewlyPinnedThumbprint = SeenThumbprint;
            return true;
        }

        return false;
    }

    public void Dispose() => _http.Dispose();
}
