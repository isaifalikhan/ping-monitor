using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Hardware;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Servers;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Credentials;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;

namespace NetWatch.Collectors.Simulator;

/// <summary>
/// Simulated management controllers (iLO for even, iDRAC for odd device IDs) and GPUs: three physical servers carry
/// NVIDIA GPUs with load curves and an occasional hot GPU; controllers report fans, PSUs, temperatures, memory and
/// drives, with rare failures (a failed fan, a predicted drive failure, a lost PSU).
/// </summary>
public static class SimulatedHardware
{
    /// <summary>The physical servers (by position among SIM servers) that carry GPUs.</summary>
    public static bool HasGpus(int serverOrdinal) => serverOrdinal is 2 or 7 or 12;

    public static List<GpuReading> Gpus(int deviceId, DateTime now, double diurnal, Random rng)
    {
        var count = deviceId % 2 == 0 ? 2 : 1;
        var hotBurst = (now.Minute + deviceId) % 47 < 4;   // a few minutes per hour run flat out and hot
        var list = new List<GpuReading>();
        for (var i = 0; i < count; i++)
        {
            var util = hotBurst ? 97 + rng.NextDouble() * 3 : Math.Clamp(20 + diurnal * 60 + rng.NextDouble() * 15, 0, 100);
            var total = 24564.0;
            var used = Math.Round(total * Math.Clamp(0.25 + util / 160, 0, 0.98));
            list.Add(new GpuReading(i, i == 0 ? "NVIDIA A10" : "NVIDIA A10", "NVIDIA", "551.78", Math.Round(util, 0), used, total,
                Math.Round(38 + util * 0.45 + (hotBurst ? 6 : 0) + rng.NextDouble() * 2), Math.Round(40 + util * 1.1, 1), Math.Round(30 + util * 0.5)));
        }

        return list;
    }

    public static HardwarePollData Controller(int deviceId, string name, DateTime now, Random rng)
    {
        var hpe = deviceId % 2 == 0;
        var data = new HardwarePollData { Source = "Redfish", Ok = true, PowerWatts = Math.Round(280 + rng.NextDouble() * 60) };
        var fanFailed = (now.Hour * 60 + now.Minute + deviceId * 13) % 600 < 15; // ~15 min every 10 h per server
        var driveWarn = deviceId % 11 == 0;
        data.Components.Add(new HardwareReading("System", name, fanFailed || driveWarn ? "Warning" : "OK", "Enabled",
            Details: hpe ? $"HPE, ProLiant DL380 Gen10 Plus, S/N CZJ{deviceId:D7}, BIOS U46 v1.62, power On" : $"Dell Inc., PowerEdge R750, S/N {deviceId:D7}, BIOS 1.10.2, power On"));
        data.Components.Add(new HardwareReading("Processor", "Processors", "OK", "Enabled", 2, "CPU", hpe ? "Intel(R) Xeon(R) Gold 6338" : "Intel(R) Xeon(R) Gold 6330"));
        data.Components.Add(new HardwareReading("Memory", "Memory", "OK", "Enabled", 512, "GiB"));
        for (var f = 1; f <= 6; f++)
        {
            var failed = fanFailed && f == 3;
            data.Components.Add(new HardwareReading("Fan", hpe ? $"Fan {f}" : $"System Board Fan{f}A", failed ? "Critical" : "OK", "Enabled",
                failed ? 0 : (hpe ? Math.Round(28 + rng.NextDouble() * 8) : Math.Round(6800 + rng.NextDouble() * 900)), hpe ? "%" : "RPM"));
        }

        data.Components.Add(new HardwareReading("Temperature", hpe ? "01-Inlet Ambient" : "System Board Inlet Temp", "OK", "Enabled", Math.Round(22 + rng.NextDouble() * 3), "C", "critical at 42 °C"));
        data.Components.Add(new HardwareReading("Temperature", hpe ? "02-CPU 1" : "CPU1 Temp", "OK", "Enabled", Math.Round(48 + rng.NextDouble() * 10), "C", "critical at 95 °C"));
        for (var p = 1; p <= 2; p++)
        {
            data.Components.Add(new HardwareReading("PowerSupply", hpe ? $"HpeServerPowerSupply {p}" : $"PS{p} Status", "OK", "Enabled", Math.Round(140 + rng.NextDouble() * 30), "W",
                hpe ? "865414-B21, 800 W" : "PWR SPLY,1400W,RDNT, 1400 W"));
        }

        data.Components.Add(new HardwareReading("StorageController", hpe ? "HPE Smart Array P408i-a SR Gen10" : "PERC H755 Front", "OK", "Enabled", Details: "FW 5.32"));
        for (var d = 0; d < 4; d++)
        {
            var warn = driveWarn && d == 2;
            data.Components.Add(new HardwareReading("Drive", hpe ? $"Drive 1I:1:{d + 1}" : $"Solid State Disk 0:1:{d}", warn ? "Warning" : "OK", "Enabled", 960, "GB",
                warn ? "MZ7KH960HAJR, SSD, failure predicted" : "MZ7KH960HAJR, SSD"));
        }

        return data;
    }

    /// <summary>Gives the simulated physical servers a BMC address and a Redfish credential (idempotent).</summary>
    public static async Task UpgradeAsync(NetWatchDbContext db, ISecretProtector protector, ILogger logger, CancellationToken ct)
    {
        var servers = await db.Devices.Include(d => d.Credentials)
            .Where(d => d.Type == DeviceType.Server && d.Tags.Any(t => t.Tag == DeviceSnapshot.SimulatedTag) && d.BmcAddress == null)
            .ToListAsync(ct);
        if (servers.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var secret = new CredentialSecret { Username = "netwatch-ro", Password = "simulated-only" };
        var profile = await db.CredentialProfiles.FirstOrDefaultAsync(p => p.Name == "Simulator iLO/iDRAC", ct);
        if (profile is null)
        {
            profile = new CredentialProfile
            {
                Name = "Simulator iLO/iDRAC",
                Kind = CredentialKind.Redfish,
                DisplayHint = secret.BuildDisplayHint(CredentialKind.Redfish),
                SecretBlob = protector.Protect(secret.ToJson()),
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.CredentialProfiles.Add(profile);
            await db.SaveChangesAsync(ct);
        }

        foreach (var d in servers)
        {
            var parts = d.IpAddress.Split('.');
            d.BmcAddress = parts.Length == 4 ? $"{parts[0]}.{parts[1]}.200.{parts[3]}" : $"bmc-{d.Name}";
            d.Credentials.Add(new DeviceCredential { Purpose = CredentialPurpose.Bmc, CredentialProfileId = profile.Id });
            d.UpdatedUtc = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Simulator: added iLO/iDRAC (Redfish) monitoring to {Count} simulated servers", servers.Count);
    }
}
