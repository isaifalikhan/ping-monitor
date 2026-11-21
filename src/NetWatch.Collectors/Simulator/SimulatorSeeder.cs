using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Credentials;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;

namespace NetWatch.Collectors.Simulator;

/// <summary>
/// Creates a realistic fake inventory (120 switches, 20 servers, 40 VMs, sites tree, dependencies, important
/// uplinks) tagged "simulated". Runs only with --simulate and only if no simulated devices exist yet.
/// </summary>
public static class SimulatorSeeder
{
    public static async Task<int> SeedAsync(NetWatchDbContext db, ISecretProtector protector, ILogger logger, CancellationToken ct)
    {
        if (await db.DeviceTags.AnyAsync(t => t.Tag == DeviceSnapshot.SimulatedTag, ct))
        {
            return 0;
        }

        var now = DateTime.UtcNow;

        // ---- sites tree
        var site = new DeviceGroup { Name = "Simulated HQ Campus", Kind = GroupKind.Site };
        var dataCenter = new DeviceGroup { Name = "Data Center", Kind = GroupKind.Building, Parent = site };
        var racks = Enumerable.Range(1, 4).Select(i => new DeviceGroup { Name = $"Rack DC-{i:D2}", Kind = GroupKind.Rack, Parent = dataCenter, SortOrder = i }).ToList();
        var buildings = new[] { "Building A", "Building B" }.Select((n, i) => new DeviceGroup { Name = n, Kind = GroupKind.Building, Parent = site, SortOrder = i + 1 }).ToList();
        var floors = buildings.SelectMany(b => Enumerable.Range(1, 3).Select(f => new DeviceGroup { Name = $"Floor {f}", Kind = GroupKind.Floor, Parent = b, SortOrder = f })).ToList();
        db.Groups.AddRange([site, dataCenter, .. racks, .. buildings, .. floors]);

        // ---- credential (the simulator ignores the community string)
        var secret = new CredentialSecret { Community = "simulated" };
        var credential = new CredentialProfile
        {
            Name = "Simulator SNMP v2c",
            Kind = CredentialKind.SnmpV2c,
            DisplayHint = secret.BuildDisplayHint(CredentialKind.SnmpV2c),
            SecretBlob = protector.Protect(secret.ToJson()),
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.CredentialProfiles.Add(credential);
        await db.SaveChangesAsync(ct);

        var devices = new List<Device>();
        Device Add(string name, string ip, DeviceType type, DeviceGroup group, string role, Device? parent)
        {
            var d = new Device
            {
                Name = name,
                IpAddress = ip,
                Type = type,
                Os = type == DeviceType.Switch ? OsKind.NetworkOs : OsKind.Windows,
                GroupId = group.Id,
                ParentDeviceId = parent?.Id,
                PollIntervalSec = 60,
                Status = DeviceStatus.Unknown,
                StatusSinceUtc = now,
                CreatedUtc = now,
                UpdatedUtc = now,
                Tags = [new DeviceTag { Tag = DeviceSnapshot.SimulatedTag }, new DeviceTag { Tag = role }],
                Credentials = [new DeviceCredential { Purpose = CredentialPurpose.Snmp, CredentialProfileId = credential.Id }],
            };
            devices.Add(d);
            db.Devices.Add(d);
            return d;
        }

        // Core and distribution must be saved first so children can reference them.
        var cores = new[] { Add("SIM-CORE-SW-01", "10.250.0.1", DeviceType.Switch, racks[0], "core", null), Add("SIM-CORE-SW-02", "10.250.0.2", DeviceType.Switch, racks[1], "core", null) };
        await db.SaveChangesAsync(ct);

        var distribution = new List<Device>();
        for (var i = 0; i < 8; i++)
        {
            distribution.Add(Add($"SIM-DIST-SW-{i + 1:D2}", $"10.250.0.{11 + i}", DeviceType.Switch, floors[i % floors.Count], "distribution", cores[i % 2]));
        }

        await db.SaveChangesAsync(ct);

        for (var i = 0; i < 110; i++)
        {
            var floor = floors[i % floors.Count];
            Add($"SIM-ACC-SW-{i + 1:D3}", $"10.250.{1 + (i / 50)}.{10 + (i % 50)}", DeviceType.Switch, floor, "access", distribution[i % distribution.Count]);
        }

        for (var i = 0; i < 20; i++)
        {
            Add($"SIM-SRV-{i + 1:D2}", $"10.250.10.{10 + i}", DeviceType.Server, racks[i % racks.Count], "server", cores[i % 2]);
        }

        for (var i = 0; i < 40; i++)
        {
            Add($"SIM-VM-{i + 1:D2}", $"10.250.20.{10 + i}", DeviceType.VM, racks[i % racks.Count], "vm", cores[i % 2]);
        }

        await db.SaveChangesAsync(ct);

        // ---- interfaces with uplinks / server NICs flagged important
        foreach (var d in devices)
        {
            foreach (var port in SimulatedTopology.PortsFor(d.Type))
            {
                db.Interfaces.Add(new NetInterface
                {
                    DeviceId = d.Id,
                    IfIndex = port.IfIndex,
                    Name = port.Name,
                    Description = port.Description,
                    IfType = port.IfType,
                    SpeedBps = port.SpeedBps,
                    IsImportant = port.IsUplink,
                    IsMonitored = true,
                    LastSeenUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Simulator seeded {Count} simulated devices", devices.Count);
        await UpgradeAsync(db, protector, logger, ct);
        return devices.Count;
    }

    /// <summary>Windows services watched on simulated Windows servers / VMs.</summary>
    public static readonly string[] WindowsServices = ["W3SVC", "Spooler", "WinRM", "EventLog"];

    /// <summary>systemd units watched on simulated Linux servers.</summary>
    public static readonly string[] LinuxServices = ["sshd", "crond", "nginx"];

    /// <summary>
    /// Phase 2 additions for an existing simulated inventory: Windows/SSH credentials, server operating systems
    /// and watched services. Idempotent.
    /// </summary>
    public static async Task UpgradeAsync(NetWatchDbContext db, ISecretProtector protector, ILogger logger, CancellationToken ct)
    {
        var servers = await db.Devices
            .Include(d => d.Credentials)
            .Include(d => d.WatchedServices)
            .Where(d => d.Tags.Any(t => t.Tag == DeviceSnapshot.SimulatedTag) && (d.Type == DeviceType.Server || d.Type == DeviceType.VM))
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
        if (servers.Count == 0 || servers.All(s => s.Credentials.Any(c => c.Purpose == CredentialPurpose.Management)))
        {
            return;
        }

        var now = DateTime.UtcNow;
        CredentialProfile Profile(string name, CredentialKind kind, CredentialSecret secret)
        {
            var existing = db.CredentialProfiles.Local.FirstOrDefault(p => p.Name == name) ?? db.CredentialProfiles.FirstOrDefault(p => p.Name == name);
            if (existing is not null)
            {
                return existing;
            }

            var p = new CredentialProfile
            {
                Name = name,
                Kind = kind,
                DisplayHint = secret.BuildDisplayHint(kind),
                SecretBlob = protector.Protect(secret.ToJson()),
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.CredentialProfiles.Add(p);
            return p;
        }

        // The simulator never connects anywhere; these secrets are placeholders.
        var windows = Profile("Simulator Windows", CredentialKind.Windows, new CredentialSecret { Username = "svc-netwatch", Domain = "SIM", Password = "simulated-only" });
        var ssh = Profile("Simulator SSH", CredentialKind.Ssh, new CredentialSecret { Username = "netwatch", Password = "simulated-only" });
        await db.SaveChangesAsync(ct);

        var physical = servers.Where(s => s.Type == DeviceType.Server).ToList();
        foreach (var d in servers)
        {
            // The last five physical servers run Linux.
            var linux = d.Type == DeviceType.Server && physical.IndexOf(d) >= physical.Count - 5;
            d.Os = linux ? OsKind.Linux : OsKind.Windows;
            d.Credentials.RemoveAll(c => c.Purpose == CredentialPurpose.Management);
            d.Credentials.Add(new DeviceCredential { Purpose = CredentialPurpose.Management, CredentialProfileId = linux ? ssh.Id : windows.Id });
            if (d.WatchedServices.Count == 0)
            {
                foreach (var name in linux ? LinuxServices : WindowsServices)
                {
                    d.WatchedServices.Add(new WatchedService { ServiceName = name, LastState = ServiceState.Unknown });
                }
            }

            d.UpdatedUtc = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Simulator: added server monitoring (WinRM/SSH, watched services) to {Count} simulated servers and VMs", servers.Count);
    }

    /// <summary>
    /// Phase 3 additions: a simulated vCenter (with a vCenter credential) and its four ESXi hosts as devices.
    /// The simulated VMs already exist (SIM-VM-01..40). Idempotent.
    /// </summary>
    public static async Task UpgradeVmwareAsync(NetWatchDbContext db, ISecretProtector protector, ILogger logger, CancellationToken ct)
    {
        if (!await db.DeviceTags.AnyAsync(t => t.Tag == DeviceSnapshot.SimulatedTag, ct) ||
            await db.Devices.AnyAsync(d => d.Type == DeviceType.VCenter && d.Tags.Any(t => t.Tag == DeviceSnapshot.SimulatedTag), ct))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var secret = new CredentialSecret { Username = "netwatch-ro@vsphere.local", Password = "simulated-only" };
        var credential = await db.CredentialProfiles.FirstOrDefaultAsync(p => p.Name == "Simulator vCenter", ct);
        if (credential is null)
        {
            credential = new CredentialProfile
            {
                Name = "Simulator vCenter",
                Kind = CredentialKind.VCenter,
                DisplayHint = secret.BuildDisplayHint(CredentialKind.VCenter),
                SecretBlob = protector.Protect(secret.ToJson()),
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.CredentialProfiles.Add(credential);
            await db.SaveChangesAsync(ct);
        }

        var snmp = await db.CredentialProfiles.FirstOrDefaultAsync(p => p.Name == "Simulator SNMP v2c", ct);
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == "Data Center", ct);
        Device Add(string name, string ip, DeviceType type, OsKind os, string role, CredentialPurpose purpose, int credentialId)
        {
            var d = new Device
            {
                Name = name,
                IpAddress = ip,
                Type = type,
                Os = os,
                GroupId = group?.Id,
                PollIntervalSec = 60,
                Status = DeviceStatus.Unknown,
                StatusSinceUtc = now,
                CreatedUtc = now,
                UpdatedUtc = now,
                Tags = [new DeviceTag { Tag = DeviceSnapshot.SimulatedTag }, new DeviceTag { Tag = role }],
                Credentials = [new DeviceCredential { Purpose = purpose, CredentialProfileId = credentialId }],
            };
            db.Devices.Add(d);
            return d;
        }

        Add("SIM-VCENTER", "10.250.30.10", DeviceType.VCenter, OsKind.Unknown, "vcenter", CredentialPurpose.VCenter, credential.Id);
        for (var i = 1; i <= 4; i++)
        {
            Add($"SIM-ESX-{i:D2}", $"10.250.30.{10 + i}", DeviceType.Hypervisor, OsKind.Esxi, "esxi", CredentialPurpose.Snmp, snmp?.Id ?? credential.Id);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Simulator: added a simulated vCenter with 4 ESXi hosts");
    }

    /// <summary>
    /// Phase 4 additions: two simulated domain controllers (SIM-DC-01 is the PDC emulator) with SNMP and the
    /// simulated Windows credential, watching the AD core services. Idempotent.
    /// </summary>
    public static async Task UpgradeDomainControllersAsync(NetWatchDbContext db, ILogger logger, CancellationToken ct)
    {
        if (!await db.DeviceTags.AnyAsync(t => t.Tag == DeviceSnapshot.SimulatedTag, ct) ||
            await db.Devices.AnyAsync(d => d.Type == DeviceType.DomainController && d.Tags.Any(t => t.Tag == DeviceSnapshot.SimulatedTag), ct))
        {
            return;
        }

        var windows = await db.CredentialProfiles.FirstOrDefaultAsync(p => p.Name == "Simulator Windows", ct);
        var snmp = await db.CredentialProfiles.FirstOrDefaultAsync(p => p.Name == "Simulator SNMP v2c", ct);
        if (windows is null || snmp is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == "Data Center", ct);
        var core = await db.Devices.FirstOrDefaultAsync(d => d.Name == "SIM-CORE-SW-01", ct);
        for (var i = 1; i <= 2; i++)
        {
            db.Devices.Add(new Device
            {
                Name = $"SIM-DC-{i:D2}",
                IpAddress = $"10.250.10.{39 + i}",
                Type = DeviceType.DomainController,
                Os = OsKind.Windows,
                GroupId = group?.Id,
                ParentDeviceId = core?.Id,
                PollIntervalSec = 60,
                Status = DeviceStatus.Unknown,
                StatusSinceUtc = now,
                CreatedUtc = now,
                UpdatedUtc = now,
                Tags = [new DeviceTag { Tag = DeviceSnapshot.SimulatedTag }, new DeviceTag { Tag = "dc" }],
                Credentials =
                [
                    new DeviceCredential { Purpose = CredentialPurpose.Snmp, CredentialProfileId = snmp.Id },
                    new DeviceCredential { Purpose = CredentialPurpose.Management, CredentialProfileId = windows.Id },
                ],
                WatchedServices = DcDefaults.WatchedServices.Select(s => new WatchedService { ServiceName = s, LastState = ServiceState.Unknown }).ToList(),
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Simulator: added two simulated domain controllers (SIM-DC-01 PDC, SIM-DC-02)");
    }
}
