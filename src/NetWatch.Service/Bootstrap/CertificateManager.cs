using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NetWatch.Core.Configuration;

namespace NetWatch.Service.Bootstrap;

/// <summary>
/// Provides the HTTPS certificate. Priority: (1) thumbprint in LocalMachine\My, (2) configured .pfx with a
/// DPAPI-protected password, (3) self-signed certificate generated on first start (SAN = host name, FQDN,
/// localhost and all local IPv4 addresses), stored in certs\ and renewed 30 days before expiry.
/// </summary>
public static class CertificateManager
{
    private const string SelfSignedFile = "netwatch-selfsigned.pfx";
    private const string PasswordSuffix = ".key";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetWatch.Certificate.v1");

    // Schannel (SslStream on Windows) cannot use EphemeralKeySet keys for a TLS server, so the key is imported into
    // a temporary CNG/CAPI container of the running account (SYSTEM for the service) that is removed again when the
    // certificate object is released. Nothing is added to the certificate store.
    private const X509KeyStorageFlags ServerKeyFlags = X509KeyStorageFlags.DefaultKeySet;

    public static X509Certificate2 Load(IConfiguration config, NetWatchPaths paths, ILogger logger)
    {
        var thumbprint = config["Https:CertificateThumbprint"]?.Replace(" ", string.Empty);
        if (!string.IsNullOrEmpty(thumbprint))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (found.Count > 0)
            {
                logger.LogInformation("Using certificate {Subject} ({Thumbprint}) from LocalMachine\\My", found[0].Subject, thumbprint);
                return found[0];
            }

            logger.LogError("Certificate with thumbprint {Thumbprint} not found in LocalMachine\\My; falling back", thumbprint);
        }

        var pfxPath = config["Https:CertificatePath"];
        if (!string.IsNullOrEmpty(pfxPath))
        {
            if (File.Exists(pfxPath))
            {
                var cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, ReadPassword(pfxPath + PasswordSuffix), ServerKeyFlags);
                logger.LogInformation("Using certificate {Subject} from {Path}, valid until {NotAfter:u}", cert.Subject, pfxPath, cert.NotAfter);
                return cert;
            }

            logger.LogError("Certificate file {Path} not found; falling back to the self-signed certificate", pfxPath);
        }

        // Imported from the web console (Settings → System status → HTTPS certificate).
        try
        {
            if (Data.Security.ConsoleCertificateStore.Load(paths, ServerKeyFlags) is { } imported)
            {
                if (imported.NotAfter > DateTime.Now)
                {
                    logger.LogInformation("Using the imported certificate {Subject}, valid until {NotAfter:u}", imported.Subject, imported.NotAfter);
                    return imported;
                }

                logger.LogError("The imported certificate {Subject} expired {NotAfter:u}; falling back to the self-signed certificate", imported.Subject, imported.NotAfter);
            }
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "The imported certificate cannot be loaded; falling back to the self-signed certificate");
        }

        return LoadOrCreateSelfSigned(paths, logger);
    }

    private static X509Certificate2 LoadOrCreateSelfSigned(NetWatchPaths paths, ILogger logger)
    {
        var path = Path.Combine(paths.CertsDir, SelfSignedFile);
        if (File.Exists(path) && File.Exists(path + PasswordSuffix))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, ReadPassword(path + PasswordSuffix), ServerKeyFlags);
                if (existing.NotAfter > DateTime.Now.AddDays(30))
                {
                    return existing;
                }

                logger.LogWarning("Self-signed certificate expires {NotAfter:u}; generating a new one", existing.NotAfter);
            }
            catch (CryptographicException ex)
            {
                logger.LogWarning(ex, "Could not load the self-signed certificate; generating a new one");
            }
        }

        var cert = CreateSelfSigned();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(paths.CertsDir);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        File.WriteAllBytes(path + PasswordSuffix, ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.LocalMachine));

        // Also export the public part so administrators can distribute it to operator PCs as trusted.
        File.WriteAllBytes(Path.Combine(paths.CertsDir, "netwatch-selfsigned.cer"), cert.Export(X509ContentType.Cert));
        logger.LogInformation("Generated self-signed HTTPS certificate {Subject}, thumbprint {Thumbprint}", cert.Subject, cert.Thumbprint);
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, password), password, ServerKeyFlags);
    }

    public static X509Certificate2 CreateSelfSigned()
    {
        var host = Dns.GetHostName();
        string fqdn;
        try
        {
            fqdn = Dns.GetHostEntry(host).HostName;
        }
        catch (SocketException)
        {
            fqdn = host;
        }

        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={fqdn}, O=NetWatch", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in new[] { host, fqdn, "localhost" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            san.AddDnsName(name);
        }

        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in LocalAddresses())
        {
            san.AddIpAddress(ip);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        // Browsers reject server certificates valid for longer than ~825 days.
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(825));
    }

    /// <summary>Stores the password of an admin-provided .pfx DPAPI-encrypted next to it (CLI --set-certificate).</summary>
    public static void StorePfxPassword(string pfxPath, string password)
    {
        // Validate first so a wrong password is reported immediately.
        using var _ = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password, X509KeyStorageFlags.EphemeralKeySet);
        File.WriteAllBytes(pfxPath + PasswordSuffix, ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.LocalMachine));
    }

    private static string? ReadPassword(string keyFile) =>
        File.Exists(keyFile) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(keyFile), Entropy, DataProtectionScope.LocalMachine)) : null;

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
    }
}
