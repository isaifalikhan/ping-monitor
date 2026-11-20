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
        return devices.Count;
    }
}
