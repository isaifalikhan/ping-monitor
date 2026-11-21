using NetWatch.Alerts;
using NetWatch.Collectors.DomainControllers;
using NetWatch.Collectors.Processing;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Simulator;
using NetWatch.Core;
using NetWatch.Core.Configuration;
using NetWatch.Core.Credentials;
using NetWatch.Core.Monitoring;

namespace NetWatch.Tests;

/// <summary>WP3: replication staleness, DC service port checks, thresholds and default DC services.</summary>
public sealed class Wp3DcHealthTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Replication_is_stale_after_the_configured_hours()
    {
        DcReplicationReading R(DateTime? success, DateTime? attempt) => new("DC02", "DC=corp,DC=local", success, attempt, 0, 0);
        Assert.False(DcResultProcessor.IsReplicationStale(R(Now.AddHours(-2.9), Now), Now, 3));
        Assert.True(DcResultProcessor.IsReplicationStale(R(Now.AddHours(-3.1), Now), Now, 3));
        Assert.False(DcResultProcessor.IsReplicationStale(R(Now.AddHours(-5), Now), Now, 6));
        Assert.True(DcResultProcessor.IsReplicationStale(R(null, Now), Now, 3));   // attempted, never succeeded
        Assert.False(DcResultProcessor.IsReplicationStale(R(null, null), Now, 3)); // brand-new partner, never attempted
    }

    [Fact]
    public void Stale_hours_setting_is_clamped()
    {
        var s = new DcSettings { ReplicationStaleHours = 0 };
        s.Normalise();
        Assert.Equal(1, s.ReplicationStaleHours);
        Assert.Equal(3, new DcSettings().ReplicationStaleHours);
        Assert.True(new DcSettings().CheckServicePorts);
    }

    [Fact]
    public void Default_rules_match_the_brief()
    {
        var rules = DefaultRules.Create(DateTime.UtcNow);
        var failing = Assert.Single(rules, r => r.Name == "AD replication failing");
        Assert.Equal(3600, failing.DurationSec);
        var stale = Assert.Single(rules, r => r.Name == "AD replication stale");
        Assert.Equal(StateKeys.DcReplicationStale, stale.Key);
        var ports = Assert.Single(rules, r => r.Name == "DC service port unreachable");
        Assert.Equal((StateKeys.DcPortUnreachable, 120), (ports.Key, ports.DurationSec));
        var skew = Assert.Single(rules, r => r.Name == "DC clock skew critical");
        Assert.Equal(120, skew.Threshold); // 2 minutes, well inside the 5-minute Kerberos tolerance
        Assert.Contains(rules, r => r.Name == "Directory/DFS replication error");
    }

    [Fact]
    public async Task Port_check_reports_each_service_port()
    {
        // Real sockets against loopback: every port is reported, in order, with an error text when it does not answer.
        var checks = await DcPorts.CheckAsync("127.0.0.1", TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(["LDAP", "Kerberos", "DNS"], checks.Select(c => c.Service));
        Assert.Equal([389, 88, 53], checks.Select(c => c.Port));
        Assert.All(checks, c => Assert.True(c.Error is null || c.Error.Length > 0));
    }

    [Fact]
    public void Simulated_dc_reports_service_ports()
    {
        var dc = new SimulatedDomainController(3, "SIM-DC-02", isPdc: false, "SIM-DC-01", Now.AddHours(-1));
        var credential = new ManagementCredential(CredentialKind.Windows, new CredentialSecret { Username = "svc", Password = "simulated-only" });
        var request = new DcPollRequest("10.0.0.2", credential, new Dictionary<string, long>(), true, 5000, true, 5985, false, true, TimeSpan.FromSeconds(10), CheckPorts: true);
        var data = dc.Poll(request, Now, 1, 60);
        Assert.NotNull(data.Ports);
        Assert.Equal(3, data.Ports.Count);
        Assert.Null(dc.Poll(request with { CheckPorts = false }, Now.AddMinutes(1), 1, 60).Ports);
    }

    [Fact]
    public void Default_dc_services_cover_ad_dns_kerberos_sysvol_and_time() =>
        Assert.Equal(["NTDS", "DNS", "Netlogon", "Kdc", "DFSR", "W32Time"], DcDefaults.WatchedServices);
}
