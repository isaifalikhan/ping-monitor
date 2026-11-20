using NetWatch.Core;

namespace NetWatch.Collectors.Simulator;

public sealed record SimPortSpec(int IfIndex, string Name, string Description, long SpeedBps, int IfType, bool IsUplink);

public sealed record SimVendorSpec(string Vendor, string Model, string Firmware, string SysObjectId, string SysDescription);

/// <summary>Shared definitions so the seeder and the simulated poller agree on ports and vendors.</summary>
public static class SimulatedTopology
{
    private const long Gig = 1_000_000_000;

    public static IReadOnlyList<SimPortSpec> PortsFor(DeviceType type) => type switch
    {
        DeviceType.Switch => Enumerable.Range(1, 48)
            .Select(i => new SimPortSpec(i, $"Gi1/0/{i}", $"GigabitEthernet1/0/{i}", Gig, 6, false))
            .Concat(Enumerable.Range(1, 4).Select(i => new SimPortSpec(48 + i, $"Te1/1/{i}", $"TenGigabitEthernet1/1/{i}", 10 * Gig, 6, true)))
            .ToList(),
        DeviceType.Server => [new SimPortSpec(1, "NIC1", "Embedded NIC 1", 10 * Gig, 6, true), new SimPortSpec(2, "NIC2", "Embedded NIC 2", 10 * Gig, 6, true)],
        DeviceType.VM => [new SimPortSpec(1, "Ethernet0", "vmxnet3 Ethernet Adapter", 10 * Gig, 6, true)],
        _ => [new SimPortSpec(1, "eth0", "eth0", Gig, 6, true), new SimPortSpec(2, "eth1", "eth1", Gig, 6, false)],
    };

    public static SimVendorSpec VendorFor(DeviceType type, int deviceId) => type switch
    {
        DeviceType.Switch => (deviceId % 3) switch
        {
            0 => new SimVendorSpec("Cisco", "C9300-48P", "17.9.4a", "1.3.6.1.4.1.9.1.2494",
                "Cisco IOS Software [Cupertino], Catalyst L3 Switch Software (CAT9K_IOSXE), Version 17.9.4a"),
            1 => new SimVendorSpec("HP", "Aruba 2930F-48G-4SFP+ (JL254A)", "WC.16.11.0012", "1.3.6.1.4.1.11.2.3.7.11.181",
                "Aruba JL254A 2930F-48G-4SFP+ Switch, revision WC.16.11.0012"),
            _ => new SimVendorSpec("Juniper", "EX4300-48T", "21.4R3-S5", "1.3.6.1.4.1.2636.1.1.1.2.63",
                "Juniper Networks, Inc. ex4300-48t Ethernet Switch, kernel JUNOS 21.4R3-S5"),
        },
        DeviceType.Server => new SimVendorSpec("Dell", "PowerEdge R750", "BIOS 1.10.2", "1.3.6.1.4.1.311.1.1.3.1.2",
            "Hardware: Intel64 Family 6 - Software: Windows Version 6.3 (Build 20348 Multiprocessor Free)"),
        DeviceType.VM => new SimVendorSpec("VMware", "VMware Virtual Platform", "VMware Tools 12.3", "1.3.6.1.4.1.311.1.1.3.1.2",
            "Hardware: Intel64 Family 6 - Software: Windows Version 6.3 (Build 17763 Multiprocessor Free)"),
        _ => new SimVendorSpec("Generic", "Generic device", "1.0", "1.3.6.1.4.1.8072.3.2.10", "Linux generic 5.15"),
    };
}
