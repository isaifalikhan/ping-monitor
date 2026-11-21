using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetWatch.Core.Configuration;
using NetWatch.Data.Security;

namespace NetWatch.Tests;

/// <summary>WP11: console certificate import checks and the air-gap TLS revocation policy.</summary>
public sealed class Wp11HardeningTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static byte[] Pfx(int rsaBits = 2048, string eku = "1.3.6.1.5.5.7.3.1", DateTime? notBefore = null, DateTime? notAfter = null, bool withKey = true, string dns = "netwatch.corp.local")
    {
        using var rsa = RSA.Create(rsaBits);
        var req = new CertificateRequest($"CN={dns}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dns);
        san.AddIpAddress(System.Net.IPAddress.Parse("10.0.0.5"));
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(notBefore ?? Now.AddDays(-1), notAfter ?? Now.AddYears(1));
        if (withKey)
        {
            return cert.Export(X509ContentType.Pfx, "pfx-pass");
        }

        // The public certificate alone, packed as a PFX (what an export "without the private key" gives).
        using var publicOnly = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        return publicOnly.Export(X509ContentType.Pfx, "pfx-pass");
    }

    [Fact]
    public void Accepts_a_server_certificate_and_reports_names_and_warnings()
    {
        var check = ConsoleCertificateStore.Validate(Pfx(), "pfx-pass", ["netwatch.corp.local"], Now);
        Assert.Contains("netwatch.corp.local", check.Names);
        Assert.Contains("10.0.0.5", check.Names);
        Assert.True(check.SelfSigned);
        Assert.Single(check.Warnings); // only "self-signed"

        var other = ConsoleCertificateStore.Validate(Pfx(), "pfx-pass", ["nms01"], Now);
        Assert.Contains(other.Warnings, w => w.Contains("name warning", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wrong-password", "password is wrong")]
    public void Rejects_a_wrong_password(string password, string message) =>
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => ConsoleCertificateStore.Validate(Pfx(), password, [], Now)).Message);

    [Fact]
    public void Rejects_unusable_certificates()
    {
        Assert.Contains("expired", Assert.Throws<ArgumentException>(() =>
            ConsoleCertificateStore.Validate(Pfx(notBefore: Now.AddYears(-2), notAfter: Now.AddDays(-1)), "pfx-pass", [], Now)).Message);
        Assert.Contains("server authentication", Assert.Throws<ArgumentException>(() =>
            ConsoleCertificateStore.Validate(Pfx(eku: "1.3.6.1.5.5.7.3.2"), "pfx-pass", [], Now)).Message);
        Assert.Contains("2048", Assert.Throws<ArgumentException>(() =>
            ConsoleCertificateStore.Validate(Pfx(rsaBits: 1024), "pfx-pass", [], Now)).Message);
        Assert.Contains("private key", Assert.Throws<ArgumentException>(() =>
            ConsoleCertificateStore.Validate(Pfx(withKey: false), "pfx-pass", [], Now)).Message);
        Assert.Contains("not valid before", Assert.Throws<ArgumentException>(() =>
            ConsoleCertificateStore.Validate(Pfx(notBefore: Now.AddDays(2), notAfter: Now.AddYears(1)), "pfx-pass", [], Now)).Message);
    }

    [Fact]
    public void Tls_policy_never_goes_online()
    {
        var s = new SecuritySettings();
        s.Normalise();
        Assert.Equal(RevocationCheck.NoCheck, TlsPolicy.Revocation);
        var policy = TlsPolicy.ChainPolicy();
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
        Assert.True(policy.DisableCertificateDownloads);

        s.CertificateRevocation = RevocationCheck.Offline;
        s.Normalise();
        Assert.Equal(X509RevocationMode.Offline, TlsPolicy.ChainPolicy().RevocationMode);
        Assert.Equal(X509RevocationMode.Offline, TlsPolicy.Apply(new System.Net.Security.SslClientAuthenticationOptions()).CertificateRevocationCheckMode);
        Assert.NotEqual(X509RevocationMode.Online, TlsPolicy.Mode);
        new SecuritySettings().Normalise(); // back to the default for other tests
    }
}
