using System.Net;
using System.Net.Sockets;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Snmp;
using NetWatch.Core;
using NetWatch.Core.Credentials;
using NetWatch.Web.Diagnostics;

namespace NetWatch.Tests;

/// <summary>Regression tests for bugs found in the 2.0.0 release QA (docs/qa/BUGS.md) that have no better home.</summary>
public sealed class QaRegressionTests
{
    /// <summary>BUG-004: SNMP to a host without an agent showed "An existing connection was forcibly closed by the remote host".</summary>
    [Fact]
    public async Task Snmp_to_a_closed_udp_port_explains_that_the_agent_is_not_running()
    {
        // A UDP port that is certainly closed on this machine.
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        var client = new SnmpClient(IPAddress.Loopback, new SnmpCredential(CredentialKind.SnmpV2c, new CredentialSecret { Community = "public" }), 1500, 0, port);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => SystemInfoProbe.ProbeAsync(client, CancellationToken.None));
        var hint = ConnectionTestService.SnmpHint(ex);
        Assert.True(hint.Contains("UDP 161 is closed", StringComparison.Ordinal) || hint.Contains("check the community", StringComparison.Ordinal), hint);
        Assert.DoesNotContain("forcibly closed", hint, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("UDP 161 is closed", ConnectionTestService.SnmpHint(new Exception("wrapped", new SocketException((int)SocketError.ConnectionReset))));
        Assert.Contains("UDP 161 is closed", ConnectionTestService.SnmpHint(new Exception("An existing connection was forcibly closed by the remote host.")));
        Assert.Contains("check the community/v3 user", ConnectionTestService.SnmpHint(new TimeoutException("Request timed out")));
        Assert.Equal("authorizationError", ConnectionTestService.SnmpHint(new Exception("authorizationError")));
    }
}
