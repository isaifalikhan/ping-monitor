using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetWatch.Core;
using NetWatch.Core.Configuration;
using NetWatch.Core.Monitoring;

namespace NetWatch.Tests;

/// <summary>
/// Release QA (TEST_PLAN CORE-01…CORE-06): the domain model. Every settings class survives hostile values (the
/// settings API stores what the browser sends) and JSON round trips; every entity keeps what is assigned and
/// round-trips through JSON (backups, settings, API); severity mappings, metric display names and the air-gap TLS
/// policy behave as documented.
/// </summary>
public sealed class QaCoreTests
{
    private static readonly Assembly Core = typeof(Severity).Assembly;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, ReferenceHandler = ReferenceHandler.IgnoreCycles };

    private static IEnumerable<Type> SettingsTypes() => Core.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && t.Namespace == "NetWatch.Core.Configuration" && t.GetMethod("Normalise", Type.EmptyTypes) is not null);

    private static object? Sample(Type t, int variant)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u switch
        {
            _ when u == typeof(string) => variant switch { 0 => "", 1 => new string('x', 5000), _ => "Ünïcode <script>'\"" },
            _ when u == typeof(int) => variant switch { 0 => int.MinValue, 1 => int.MaxValue, _ => 7 },
            _ when u == typeof(long) => variant switch { 0 => long.MinValue, 1 => long.MaxValue, _ => 7L },
            _ when u == typeof(double) => variant switch { 0 => double.MinValue, 1 => double.MaxValue, _ => 7.5 },
            _ when u == typeof(bool) => variant != 0,
            _ when u == typeof(DateTime) => new DateTime(2026, 11, 22, 1, 2, 3, DateTimeKind.Utc),
            _ when u == typeof(Guid) => Guid.Parse("11111111-2222-3333-4444-555555555555"),
            _ when u.IsEnum => Enum.GetValues(u).GetValue(variant % Enum.GetValues(u).Length),
            _ => null,
        };
    }

    [Fact]
    public void Every_settings_class_normalises_hostile_values_into_a_usable_range()
    {
        var types = SettingsTypes().ToList();
        Assert.True(types.Count >= 10, $"only {types.Count} settings classes");
        foreach (var type in types)
        {
            for (var variant = 0; variant < 3; variant++)
            {
                var s = Activator.CreateInstance(type)!;
                foreach (var p in type.GetProperties().Where(p => p.CanWrite))
                {
                    if (Sample(p.PropertyType, variant) is { } v)
                    {
                        p.SetValue(s, v);
                    }
                }

                type.GetMethod("Normalise")!.Invoke(s, null);

                // Normalising is idempotent and the result round-trips through the stored JSON.
                var once = JsonSerializer.Serialize(s, type, Json);
                type.GetMethod("Normalise")!.Invoke(s, null);
                Assert.Equal(once, JsonSerializer.Serialize(s, type, Json));
                var back = JsonSerializer.Deserialize(once, type, Json)!;
                Assert.Equal(once, JsonSerializer.Serialize(back, type, Json));

                // Ports, intervals, timeouts and counts never end up negative or zero-length-impossible.
                foreach (var p in type.GetProperties().Where(p => p.PropertyType == typeof(int) && p.CanRead))
                {
                    var value = (int)p.GetValue(s)!;
                    if (p.Name.EndsWith("Port", StringComparison.Ordinal))
                    {
                        Assert.InRange(value, 1, 65535);
                    }
                    else if (p.Name.EndsWith("Sec", StringComparison.Ordinal) || p.Name.EndsWith("Ms", StringComparison.Ordinal) || p.Name.EndsWith("Days", StringComparison.Ordinal) || p.Name.EndsWith("Min", StringComparison.Ordinal))
                    {
                        Assert.True(value >= 0, $"{type.Name}.{p.Name} = {value} after Normalise (variant {variant})");
                    }
                }
            }
        }
    }

    [Fact]
    public void Every_entity_keeps_assigned_values_and_round_trips_through_json()
    {
        var entities = Core.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && t.Namespace == "NetWatch.Core.Entities" && t.GetConstructor(Type.EmptyTypes) is not null).ToList();
        Assert.True(entities.Count >= 30, $"only {entities.Count} entities");
        foreach (var type in entities)
        {
            var e = Activator.CreateInstance(type)!;
            foreach (var p in type.GetProperties().Where(p => p.CanWrite && p.CanRead))
            {
                if (Sample(p.PropertyType, 2) is { } v)
                {
                    p.SetValue(e, v);
                    Assert.Equal(v, p.GetValue(e));
                }
                else
                {
                    _ = p.GetValue(e); // navigation / collection defaults must not throw
                }
            }

            var json = JsonSerializer.Serialize(e, type, Json);
            var back = JsonSerializer.Deserialize(json, type, Json)!;
            Assert.Equal(json, JsonSerializer.Serialize(back, type, Json));
        }
    }

    [Fact]
    public void Public_records_of_the_core_are_value_equal_and_printable()
    {
        foreach (var type in Core.GetTypes().Where(t => t.IsPublic && !t.IsAbstract && !t.IsGenericTypeDefinition && t.GetMethod("<Clone>$") is not null || (t.IsPublic && t.IsValueType && !t.IsEnum && t.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance) is not null)))
        {
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (ctor is null)
            {
                continue;
            }

            var args = ctor.GetParameters().Select(p => Sample(p.ParameterType, 2) ?? (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)).ToArray();
            object a, b;
            try
            {
                a = ctor.Invoke(args);
                b = ctor.Invoke(args);
            }
            catch (TargetInvocationException)
            {
                continue; // records that validate their arguments
            }

            if (args.All(x => x is null || x is string || x.GetType().IsValueType))
            {
                Assert.Equal(a, b);
                Assert.Equal(a.GetHashCode(), b.GetHashCode());
            }

            Assert.False(string.IsNullOrEmpty(a.ToString()));
        }
    }

    [Theory]
    [InlineData(0, Severity.Critical)]
    [InlineData(2, Severity.Critical)]
    [InlineData(3, Severity.Major)]
    [InlineData(4, Severity.Warning)]
    [InlineData(5, Severity.Info)]
    [InlineData(7, Severity.Info)]
    public void Syslog_severity_maps_to_alert_severity(int syslog, Severity expected) => Assert.Equal(expected, SeverityMapping.FromSyslog(syslog));

    [Fact]
    public void Windows_levels_and_syslog_names_map_as_documented()
    {
        Assert.Equal([Severity.Info, Severity.Critical, Severity.Major, Severity.Warning, Severity.Info, Severity.Info], Enumerable.Range(0, 6).Select(SeverityMapping.FromWindowsLevel).ToArray());
        Assert.Equal(["Emergency", "Alert", "Critical", "Error", "Warning", "Notice", "Informational", "Debug", "9"], new[] { 0, 1, 2, 3, 4, 5, 6, 7, 9 }.Select(SeverityMapping.SyslogSeverityName).ToArray());
    }

    [Fact]
    public void Every_metric_state_and_event_key_has_a_display_name()
    {
        foreach (var (type, names) in new[] { (typeof(MetricKeys), MetricKeys.DisplayNames), (typeof(StateKeys), StateKeys.DisplayNames), (typeof(EventKeys), EventKeys.DisplayNames) })
        {
            var keys = type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
            Assert.NotEmpty(keys);
            Assert.All(keys, k => Assert.True(names.ContainsKey(k), $"{type.Name}: no display name for {k}"));
            Assert.All(names.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        }
    }

    [Fact]
    public void Tls_policy_never_goes_online_and_rejects_untrusted_or_mismatched_certificates()
    {
        var before = TlsPolicy.Revocation;
        try
        {
            TlsPolicy.Revocation = RevocationCheck.NoCheck;
            Assert.Equal(X509RevocationMode.NoCheck, TlsPolicy.Mode);
            TlsPolicy.Revocation = RevocationCheck.Offline;
            Assert.Equal(X509RevocationMode.Offline, TlsPolicy.Mode);
            var policy = TlsPolicy.ChainPolicy();
            Assert.True(policy.DisableCertificateDownloads);
            Assert.Equal(X509RevocationMode.Offline, policy.RevocationMode);
            Assert.True(policy.UrlRetrievalTimeout <= TimeSpan.FromSeconds(2));
            var options = TlsPolicy.Apply(new System.Net.Security.SslClientAuthenticationOptions());
            Assert.Equal(X509RevocationMode.Offline, options.CertificateRevocationCheckMode);
            Assert.True(options.CertificateChainPolicy!.DisableCertificateDownloads);

            // A self-signed certificate is not trusted: refused with a reason, quickly (no network wait).
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=vcenter.qa.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            var started = DateTime.UtcNow;
            Assert.False(TlsPolicy.Verify(cert, "vcenter.qa.local", out var error));
            Assert.False(string.IsNullOrEmpty(error));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        }
        finally
        {
            TlsPolicy.Revocation = before;
        }
    }
}
