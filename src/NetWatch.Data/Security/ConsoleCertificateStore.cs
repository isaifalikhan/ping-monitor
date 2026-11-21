using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NetWatch.Core.Configuration;

namespace NetWatch.Data.Security;

/// <summary>Facts about an uploaded console certificate, shown before it is used.</summary>
public sealed record ConsoleCertificateCheck(string Subject, string Issuer, string Thumbprint, DateTime NotBeforeUtc, DateTime NotAfterUtc, IReadOnlyList<string> Names, bool SelfSigned,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The HTTPS certificate imported from the web console (typically issued by the organisation's internal CA):
/// <c>certs/console.pfx</c> with its password DPAPI-protected (machine scope) in <c>console.pfx.key</c>. It is
/// used at the next start unless appsettings.json names another certificate (Https:CertificateThumbprint/Path).
/// </summary>
public static class ConsoleCertificateStore
{
    public const string FileName = "console.pfx";
    public const string PasswordSuffix = ".key";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>Same entropy as the CLI's --set-certificate so both stores read alike.</summary>
    public static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetWatch.Certificate.v1");

    public static string PathIn(NetWatchPaths paths) => Path.Combine(paths.CertsDir, FileName);

    /// <summary>Checks a PFX (throws <see cref="ArgumentException"/> with the reason when it cannot be used for HTTPS).</summary>
    public static ConsoleCertificateCheck Validate(byte[] pfx, string password, IEnumerable<string> hostNames, DateTime nowUtc)
    {
        X509Certificate2 cert;
        try
        {
            cert = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException($"The file is not a PFX/PKCS#12 certificate or the password is wrong ({ex.Message.Trim()}).");
        }

        using (cert)
        {
            if (!cert.HasPrivateKey)
            {
                throw new ArgumentException("The PFX has no private key: export it again with the private key.");
            }

            if (cert.NotAfter.ToUniversalTime() <= nowUtc)
            {
                throw new ArgumentException($"The certificate expired on {cert.NotAfter.ToUniversalTime():u}.");
            }

            if (cert.NotBefore.ToUniversalTime() > nowUtc.AddMinutes(5))
            {
                throw new ArgumentException($"The certificate is not valid before {cert.NotBefore.ToUniversalTime():u} (check this server's clock).");
            }

            var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            if (eku is not null && eku.EnhancedKeyUsages.Cast<Oid>().All(o => o.Value != ServerAuthOid))
            {
                throw new ArgumentException("The certificate is not allowed for server authentication (Enhanced Key Usage lacks 1.3.6.1.5.5.7.3.1).");
            }

            var keyBits = cert.GetRSAPublicKey()?.KeySize ?? cert.GetECDsaPublicKey()?.KeySize ?? 0;
            if (cert.GetRSAPublicKey() is not null && keyBits < 2048)
            {
                throw new ArgumentException($"The RSA key has {keyBits} bits; at least 2048 are required.");
            }

            var names = new List<string>();
            foreach (var san in cert.Extensions.OfType<X509SubjectAlternativeNameExtension>())
            {
                names.AddRange(san.EnumerateDnsNames());
                names.AddRange(san.EnumerateIPAddresses().Select(i => i.ToString()));
            }

            var warnings = new List<string>();
            var hosts = hostNames.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (hosts.Count > 0 && !hosts.Any(h => cert.MatchesHostname(h, allowWildcards: true, allowCommonName: false)))
            {
                warnings.Add($"None of this server's names ({string.Join(", ", hosts)}) is in the certificate: browsers will show a name warning.");
            }

            if (cert.NotAfter.ToUniversalTime() < nowUtc.AddDays(30))
            {
                warnings.Add($"The certificate expires soon ({cert.NotAfter.ToUniversalTime():u}).");
            }

            var selfSigned = cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData);
            if (selfSigned)
            {
                warnings.Add("The certificate is self-signed: operator PCs must trust it explicitly.");
            }

            return new ConsoleCertificateCheck(cert.Subject, cert.Issuer, cert.Thumbprint, cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(), names, selfSigned, warnings);
        }
    }

    /// <summary>Validates and stores the PFX; the previous imported certificate is kept as console.pfx.old.</summary>
    public static ConsoleCertificateCheck Import(byte[] pfx, string password, NetWatchPaths paths, IEnumerable<string> hostNames)
    {
        var check = Validate(pfx, password, hostNames, DateTime.UtcNow);
        Directory.CreateDirectory(paths.CertsDir);
        var path = PathIn(paths);
        if (File.Exists(path))
        {
            File.Copy(path, path + ".old", overwrite: true);
        }

        File.WriteAllBytes(path, pfx);
        File.WriteAllBytes(path + PasswordSuffix, ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.LocalMachine));
        return check;
    }

    public static bool Remove(NetWatchPaths paths)
    {
        var path = PathIn(paths);
        var existed = File.Exists(path);
        foreach (var f in new[] { path, path + PasswordSuffix })
        {
            if (File.Exists(f))
            {
                File.Delete(f);
            }
        }

        return existed;
    }

    /// <summary>Loads the imported certificate for Kestrel, or null when none was imported.</summary>
    public static X509Certificate2? Load(NetWatchPaths paths, X509KeyStorageFlags flags)
    {
        var path = PathIn(paths);
        if (!File.Exists(path) || !File.Exists(path + PasswordSuffix))
        {
            return null;
        }

        var password = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path + PasswordSuffix), Entropy, DataProtectionScope.LocalMachine));
        return X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
    }
}
