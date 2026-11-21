using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace NetWatch.Core.Configuration;

/// <summary>How certificate revocation is checked for outgoing TLS (vCenter, iLO/iDRAC, LDAPS, SMTP).</summary>
public enum RevocationCheck
{
    /// <summary>Default for air-gapped networks: never contact a CRL/OCSP responder (it would hang until a timeout).</summary>
    NoCheck,

    /// <summary>Only CRLs already in the local cache of the NetWatch server (import the internal CA's CRL with certutil).</summary>
    Offline,
}

/// <summary>
/// Outgoing TLS policy shared by all clients: revocation per <see cref="SecuritySettings.CertificateRevocation"/>,
/// no certificate or CRL downloads (AIA/CDP URLs), a short retrieval timeout. Set once at start-up and on every
/// save of the security settings.
/// </summary>
public static class TlsPolicy
{
    private static volatile int _mode = (int)RevocationCheck.NoCheck;

    public static RevocationCheck Revocation
    {
        get => (RevocationCheck)_mode;
        set => _mode = (int)value;
    }

    public static X509RevocationMode Mode => Revocation == RevocationCheck.Offline ? X509RevocationMode.Offline : X509RevocationMode.NoCheck;

    /// <summary>Chain policy for SslClientAuthenticationOptions / X509Chain: nothing is ever fetched from the network.</summary>
    public static X509ChainPolicy ChainPolicy() => new()
    {
        RevocationMode = Mode,
        RevocationFlag = X509RevocationFlag.ExcludeRoot,
        DisableCertificateDownloads = true,
        UrlRetrievalTimeout = TimeSpan.FromSeconds(2),
    };

    /// <summary>Applies the policy to an HTTPS / TLS client.</summary>
    public static SslClientAuthenticationOptions Apply(SslClientAuthenticationOptions options)
    {
        options.CertificateRevocationCheckMode = Mode;
        options.CertificateChainPolicy = ChainPolicy();
        return options;
    }

    /// <summary>Full verification for clients without a built-in one (LDAPS callback): chain under the policy plus host name.</summary>
    public static bool Verify(X509Certificate certificate, string host, out string? error)
    {
        using var cert = new X509Certificate2(certificate);
        using var chain = new X509Chain { ChainPolicy = ChainPolicy() };
        if (!chain.Build(cert))
        {
            error = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
            return false;
        }

        if (!cert.MatchesHostname(host, allowWildcards: true, allowCommonName: true))
        {
            error = $"the certificate is not issued for {host}";
            return false;
        }

        error = null;
        return true;
    }
}
