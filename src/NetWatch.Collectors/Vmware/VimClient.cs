using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace NetWatch.Collectors.Vmware;

/// <summary>SOAP fault returned by vCenter/ESXi (e.g. InvalidLogin, NotAuthenticated, NoPermission).</summary>
public sealed class VimFaultException : Exception
{
    public VimFaultException(string faultType, string message)
        : base(message)
    {
        FaultType = faultType;
    }

    public string FaultType { get; }
}

/// <summary>A vSphere managed object reference (type + value), e.g. VirtualMachine:vm-42.</summary>
public readonly record struct MoRef(string Type, string Value)
{
    public override string ToString() => $"{Type}:{Value}";

    public XElement ToElement(string name) => new(Vim.Ns + name, new XAttribute("type", Type), Value);

    public static MoRef? From(XElement? e) =>
        e is null || string.IsNullOrEmpty(e.Value) ? null : new MoRef((string?)e.Attribute("type") ?? string.Empty, e.Value.Trim());
}

/// <summary>vim25 XML helpers.</summary>
public static class Vim
{
    public static readonly XNamespace Ns = "urn:vim25";
    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>Creates a vim25 element.</summary>
    public static XElement E(string name, params object?[] content) => new(Ns + name, content.Where(c => c is not null).ToArray()!);

    /// <summary>Element with an xsi:type (for polymorphic arguments such as TraversalSpec).</summary>
    public static XElement Typed(string name, string type, params object?[] content) =>
        new(Ns + name, new XAttribute(Xsi + "type", type), content.Where(c => c is not null).ToArray()!);

    public static string? Child(this XElement e, string name) => e.Element(Ns + name)?.Value;

    public static IEnumerable<XElement> Children(this XElement e, string name) => e.Elements(Ns + name);

    public static string? XsiType(this XElement e) => (string?)e.Attribute(Xsi + "type");
}

/// <summary>
/// Minimal vSphere Web Services (vim25 SOAP) client over HTTPS for the calls NetWatch needs. Uses the session
/// cookie returned by Login. Certificates: CA-trusted certificates are accepted; otherwise the SHA-256 thumbprint
/// must equal the pinned one, or (trust on first use) is pinned now.
/// </summary>
public sealed class VimClient : IDisposable
{
    // The SOAP action selects the API version; 7.0 is understood by vCenter/ESXi 7.0 and 8.0.
    private const string SoapAction = "urn:vim25/7.0";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string? _pinned;
    private readonly bool _trustOnFirstUse;

    public VimClient(string host, string? pinnedThumbprint, bool trustOnFirstUse, TimeSpan timeout)
    {
        _pinned = pinnedThumbprint;
        _trustOnFirstUse = trustOnFirstUse;
        var target = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        _endpoint = new Uri($"https://{target}/sdk");
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            SslOptions = Core.Configuration.TlsPolicy.Apply(new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate }),
        };
        _http = new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>Thumbprint presented by the server (SHA-256, hex).</summary>
    public string? SeenThumbprint { get; private set; }

    /// <summary>Set when the certificate was accepted by trust on first use and should be pinned.</summary>
    public string? NewlyPinnedThumbprint { get; private set; }

    public static string Thumbprint(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return false;
        }

        SeenThumbprint = Thumbprint(certificate);
        if (_pinned is not null)
        {
            // A pinned certificate must match exactly, even if a CA would vouch for another one.
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

    /// <summary>Calls a vim25 method and returns the "...Response" element.</summary>
    public async Task<XElement> InvokeAsync(string method, MoRef @this, IEnumerable<XElement> arguments, CancellationToken ct)
    {
        var envelope = new XElement(Vim.Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", Vim.Soap),
            new XAttribute(XNamespace.Xmlns + "xsi", Vim.Xsi),
            new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
            new XElement(Vim.Soap + "Body",
                new XElement(Vim.Ns + method, @this.ToElement("_this"), arguments)));

        using var content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
        request.Headers.Add("SOAPAction", SoapAction);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new VimFaultException("CertificateRejected", _pinned is not null && SeenThumbprint is not null
                ? $"The vCenter certificate changed (pinned SHA-256 {_pinned}, presented {SeenThumbprint}). If it was renewed on purpose, clear the pinned certificate on the device page."
                : "The vCenter certificate is not trusted (enable trust on first use, or install the VMCA root certificate on this server).");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            XDocument doc;
            try
            {
                doc = XDocument.Parse(text);
            }
            catch (System.Xml.XmlException)
            {
                throw new VimFaultException("HttpError", $"Unexpected response from {_endpoint.Host}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var body = doc.Root?.Element(Vim.Soap + "Body") ?? throw new VimFaultException("Protocol", "SOAP body missing.");
            if (body.Element(Vim.Soap + "Fault") is { } fault)
            {
                var detail = fault.Element("detail")?.Elements().FirstOrDefault();
                var type = detail?.XsiType() ?? detail?.Name.LocalName.Replace("Fault", string.Empty) ?? "Fault";
                throw new VimFaultException(type, fault.Element("faultstring")?.Value ?? type);
            }

            return body.Elements().FirstOrDefault() ?? throw new VimFaultException("Protocol", $"Empty {method} response.");
        }
    }

    public void Dispose() => _http.Dispose();
}
