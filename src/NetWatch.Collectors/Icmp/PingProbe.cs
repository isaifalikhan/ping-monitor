using System.Net;
using System.Net.NetworkInformation;

namespace NetWatch.Collectors.Icmp;

public readonly record struct PingResult(bool Reachable, double? AvgRttMs, double LossPct);

/// <summary>ICMP echo probe: sends <c>count</c> echoes and reports average RTT and packet loss.</summary>
public static class PingProbe
{
    private static readonly byte[] Payload = new byte[32];

    public static async Task<PingResult> ProbeAsync(IPAddress address, int count, int timeoutMs, CancellationToken ct)
    {
        count = Math.Max(1, count);
        var received = 0;
        double totalRtt = 0;
        using var ping = new Ping();
        var options = new PingOptions(ttl: 64, dontFragment: true);

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), Payload, options, ct);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    totalRtt += reply.RoundtripTime;
                }
            }
            catch (PingException)
            {
                // Treated as a lost packet (e.g. no route to host).
            }

            if (i < count - 1)
            {
                await Task.Delay(100, ct);
            }
        }

        return new PingResult(received > 0, received > 0 ? totalRtt / received : null, (count - received) * 100.0 / count);
    }
}
