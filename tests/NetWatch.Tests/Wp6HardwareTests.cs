using System.Text;
using System.Text.Json;
using NetWatch.Alerts;
using NetWatch.Collectors.Hardware;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Servers.Windows;
using NetWatch.Collectors.Simulator;
using NetWatch.Core.Monitoring;

namespace NetWatch.Tests;

/// <summary>WP6: GPU parsing (WMI, performance counters, nvidia-smi, lspci, WinRM shell) and Redfish parsing (HPE iLO 5, Dell iDRAC 9).</summary>
public sealed class Wp6HardwareTests
{
    private static string Text(string folder, string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", folder, file));

    private static JsonElement Json(string file) => JsonDocument.Parse(Text("Redfish", file)).RootElement;

    private static WmiRow Row(params (string Key, string? Value)[] values)
    {
        var row = new WmiRow();
        foreach (var (k, v) in values)
        {
            row[k] = v;
        }

        return row;
    }

    // ------------------------------------------------------------------ GPUs

    [Fact]
    public void Video_controllers_keep_real_gpus_only()
    {
        var gpus = GpuParsers.FromVideoControllers(
        [
            Row(("Name", "Matrox G200eH3 (HPE)"), ("PNPDeviceID", @"PCI\VEN_102B&DEV_0538&SUBSYS_00E4103C&REV_02\4&2E1B3C1&0&0018")),
            Row(("Name", "Microsoft Remote Display Adapter"), ("PNPDeviceID", @"SWD\REMOTEDISPLAYENUM\RDPIDD_INDIRECTDISPLAY")),
            Row(("Name", "NVIDIA A10"), ("PNPDeviceID", @"PCI\VEN_10DE&DEV_2236&SUBSYS_148210DE&REV_A1\6&1C3E5F&0&00400008"), ("DriverVersion", "31.0.15.5178"),
                ("AdapterRAM", "4293918720"), ("Status", "OK"), ("ConfigManagerErrorCode", "0")),
            Row(("Name", "NVIDIA A10"), ("PNPDeviceID", @"PCI\VEN_10DE&DEV_2236&SUBSYS_148210DE&REV_A1\6&2A9F1B&0&00400008"), ("Status", "Error"), ("ConfigManagerErrorCode", "43")),
            Row(("Name", "AMD Radeon Pro W6800"), ("PNPDeviceID", @"PCI\VEN_1002&DEV_73A3"), ("AdapterRAM", "2147483648"), ("ConfigManagerErrorCode", "0")),
        ]);

        Assert.Equal(3, gpus.Count);
        Assert.Equal(("NVIDIA", 0, (double?)null, (string?)null), (gpus[0].Vendor!, gpus[0].Index, gpus[0].MemoryTotalMb, gpus[0].Problem)); // 4 GB cap = unknown
        Assert.Contains("43", gpus[1].Problem);
        Assert.Contains("driver reported a failure", gpus[1].Problem);
        Assert.Equal((2, "AMD", 2048.0), (gpus[2].Index, gpus[2].Vendor!, gpus[2].MemoryTotalMb!.Value));
    }

    [Fact]
    public void Performance_counters_give_utilization_and_memory_per_adapter()
    {
        var gpus = new List<GpuReading> { new(0, "NVIDIA A10", "NVIDIA"), new(1, "NVIDIA A10", "NVIDIA") };
        var engines = new[]
        {
            Row(("Name", "pid_4312_luid_0x00000000_0x0000D1F4_phys_0_eng_0_engtype_3D"), ("UtilizationPercentage", "40")),
            Row(("Name", "pid_5120_luid_0x00000000_0x0000D1F4_phys_0_eng_0_engtype_3D"), ("UtilizationPercentage", "35")),
            Row(("Name", "pid_4312_luid_0x00000000_0x0000D1F4_phys_0_eng_3_engtype_Compute_0"), ("UtilizationPercentage", "62")),
            Row(("Name", "pid_9000_luid_0x00000000_0x0000E22A_phys_1_eng_0_engtype_3D"), ("UtilizationPercentage", "3")),
        };
        var memory = new[]
        {
            Row(("Name", "luid_0x00000000_0x0000D1F4_phys_0"), ("DedicatedUsage", "19121684480")),
            Row(("Name", "luid_0x00000000_0x0000E22A_phys_1"), ("DedicatedUsage", "536870912")),
        };
        var result = GpuParsers.ApplyPerfCounters(gpus, engines, memory);
        Assert.Equal(75, result[0].UtilizationPct);   // busiest engine type: 3D 40 + 35
        Assert.Equal(18236, result[0].MemoryUsedMb);
        Assert.Equal((3.0, 512.0), (result[1].UtilizationPct!.Value, result[1].MemoryUsedMb!.Value));
    }

    [Fact]
    public void Parses_nvidia_smi_csv_and_errors()
    {
        var (gpus, error) = GpuParsers.ParseNvidiaSmi(Text("Gpu", "nvidia-smi.csv"));
        Assert.Null(error);
        Assert.Equal(2, gpus.Count);
        var g = gpus[0];
        Assert.Equal(("NVIDIA A10", 87.0, 18234.0, 23028.0, 71.0, 141.52, 54.0, "551.78"),
            (g.Name, g.UtilizationPct!.Value, g.MemoryUsedMb!.Value, g.MemoryTotalMb!.Value, g.TemperatureC!.Value, g.PowerW!.Value, g.FanPct!.Value, g.DriverVersion!));
        Assert.Equal(79.2, g.MemoryPct);

        var (na, _) = GpuParsers.ParseNvidiaSmi(Text("Gpu", "nvidia-smi-na.csv"));
        Assert.Null(na[0].PowerW);   // [N/A]
        Assert.Null(na[0].FanPct);   // passively cooled T4

        var (none, failure) = GpuParsers.ParseNvidiaSmi(Text("Gpu", "nvidia-smi-error.txt"));
        Assert.Empty(none);
        Assert.StartsWith("NVIDIA-SMI has failed", failure);
    }

    [Fact]
    public void Lspci_and_nvidia_smi_merge_on_linux()
    {
        var listed = GpuParsers.FromLspci(Text("Gpu", "lspci.txt").Split('\n'));
        Assert.Equal(2, listed.Count); // the Matrox BMC VGA is not a GPU
        Assert.All(listed, g => Assert.Equal("NVIDIA", g.Vendor));

        var (smi, _) = GpuParsers.ParseNvidiaSmi(Text("Gpu", "nvidia-smi.csv"));
        var merged = GpuParsers.MergeNvidia(listed, smi, null);
        Assert.Equal(71, merged[0].TemperatureC);
        Assert.Equal(39, merged[1].TemperatureC);

        // Driver broken: the GPUs are still listed, with the nvidia-smi error as their problem.
        var broken = GpuParsers.MergeNvidia(listed, [], "NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.");
        Assert.All(broken, g => Assert.StartsWith("nvidia-smi: NVIDIA-SMI has failed", g.Problem));
    }

    [Fact]
    public void WinRm_receive_response_yields_stdout_and_done()
    {
        var output = new StringBuilder();
        Assert.True(WinRmShell.ParseReceive(Text("Gpu", "winrm-receive.xml"), output));
        var (gpus, _) = GpuParsers.ParseNvidiaSmi(output.ToString());
        Assert.Equal(87, Assert.Single(gpus).UtilizationPct);
        Assert.False(WinRmShell.ParseReceive("<Envelope><Body><ReceiveResponse><CommandState State=\"http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Running\"/></ReceiveResponse></Body></Envelope>", new StringBuilder()));
    }

    // ------------------------------------------------------------------ Redfish

    [Fact]
    public void Parses_hpe_ilo5_system_thermal_power_and_storage()
    {
        var system = RedfishParser.System(Json("hpe-ilo5-system.json")).ToList();
        var sys = Assert.Single(system, c => c.Category == "System");
        Assert.Equal(("SRV-APP-01", "Warning"), (sys.Name, sys.Health!));
        Assert.Contains("ProLiant DL380 Gen10 Plus", sys.Details);
        Assert.Contains("S/N CZJ2240ABC", sys.Details);
        Assert.Equal(512, Assert.Single(system, c => c.Category == "Memory").Reading);
        Assert.Equal(2, Assert.Single(system, c => c.Category == "Processor").Reading);

        var thermal = RedfishParser.Thermal(Json("hpe-ilo5-thermal.json")).ToList();
        Assert.Equal(2, thermal.Count(c => c.Category == "Fan"));         // absent fan 7 skipped
        Assert.Equal(2, thermal.Count(c => c.Category == "Temperature")); // absent DIMM sensor skipped
        var fan2 = Assert.Single(thermal, c => c.Name == "Fan 2");
        Assert.True(fan2.IsProblem);
        Assert.Equal("%", fan2.Unit);
        Assert.Equal("critical at 42 °C", Assert.Single(thermal, c => c.Name == "01-Inlet Ambient").Details);

        var (psus, watts) = RedfishParser.Power(Json("hpe-ilo5-power.json"));
        Assert.Equal(312, watts);
        Assert.Equal(2, psus.Count);
        Assert.True(Assert.Single(psus, p => p.Name == "HpeServerPowerSupply 2").IsProblem);
        Assert.Contains("800 W", psus[0].Details);

        var storage = RedfishParser.Storage(Json("hpe-ilo5-storage.json"), [Json("hpe-ilo5-drive0.json"), Json("hpe-ilo5-drive1.json")]).ToList();
        Assert.Equal("OK", Assert.Single(storage, c => c.Category == "StorageController").Health);
        var bad = Assert.Single(storage, c => c.Name == "Drive 1I:1:2");
        Assert.True(bad.IsProblem);
        Assert.Contains("failure predicted", bad.Details);
        Assert.Contains("4 % life left", bad.Details);
        Assert.Equal(960, bad.Reading);
    }

    [Fact]
    public void Parses_dell_idrac9_system_thermal_power_and_storage()
    {
        var sys = Assert.Single(RedfishParser.System(Json("dell-idrac9-system.json")), c => c.Category == "System");
        Assert.Equal(("srv-db-02", "OK"), (sys.Name, sys.Health!));
        Assert.Contains("PowerEdge R750", sys.Details);

        var thermal = RedfishParser.Thermal(Json("dell-idrac9-thermal.json")).ToList();
        Assert.Equal((7080.0, "RPM"), (Assert.Single(thermal, c => c.Name == "System Board Fan1A").Reading!.Value, "RPM"));
        var cpu = Assert.Single(thermal, c => c.Name == "CPU1 Temp");
        Assert.True(cpu.IsProblem);
        Assert.Equal(91, cpu.Reading);

        var (psus, watts) = RedfishParser.Power(Json("dell-idrac9-power.json"));
        Assert.Equal(398, watts);
        Assert.All(psus, p => Assert.False(p.IsProblem));

        var storage = RedfishParser.Storage(Json("dell-idrac9-storage.json"), [Json("dell-idrac9-drive.json")]).ToList();
        Assert.False(Assert.Single(storage, c => c.Category == "StorageController").IsProblem); // controller itself OK
        Assert.True(Assert.Single(storage, c => c.Category == "Drive").IsProblem);
        Assert.Equal(1920, storage.Single(c => c.Category == "Drive").Reading);
    }

    [Fact]
    public void Follows_links_of_the_service_root_and_collections()
    {
        var root = Json("service-root.json");
        Assert.Equal("/redfish/v1/Systems/", RedfishParser.Link(root, "Systems"));
        Assert.Equal("/redfish/v1/Chassis/", RedfishParser.Link(root, "Chassis"));
        Assert.Equal(["/redfish/v1/Systems/1/Storage/DE00A000/Drives/0/", "/redfish/v1/Systems/1/Storage/DE00A000/Drives/1/"],
            RedfishParser.Links(Json("hpe-ilo5-storage.json"), "Drives"));
        Assert.Equal("/redfish/v1/Systems/System.Embedded.1/Storage", RedfishParser.Link(Json("dell-idrac9-system.json"), "Storage"));
    }

    [Theory]
    [InlineData(2L, "OK", 3L, "OK")]
    [InlineData(3L, "Warning", 4L, "Warning")]
    [InlineData(4L, "Critical", 5L, "Critical")]
    [InlineData(1L, null, 2L, null)]
    public void Snmp_fallback_maps_hpe_and_dell_status(long hpe, string? hpeHealth, long dell, string? dellHealth)
    {
        Assert.Equal(hpeHealth, HardwareSnmp.HpeHealth(hpe));
        Assert.Equal(dellHealth, HardwareSnmp.DellHealth(dell));
    }

    [Fact]
    public void Absent_and_ok_components_are_not_problems()
    {
        Assert.False(new HardwareReading("Fan", "Fan 7", "Critical", "Absent").IsProblem);
        Assert.False(new HardwareReading("Fan", "Fan 1", "OK", "Enabled").IsProblem);
        Assert.False(new HardwareReading("Fan", "Fan 1", null, "Enabled").IsProblem);
        Assert.True(new HardwareReading("Drive", "Disk 2", "Warning", "Enabled").IsProblem);
    }

    // ------------------------------------------------------------------ simulator and rules

    [Fact]
    public void Simulator_produces_gpus_on_three_servers_and_controller_data()
    {
        Assert.Equal(3, Enumerable.Range(0, 20).Count(SimulatedHardware.HasGpus));
        var gpus = SimulatedHardware.Gpus(12, new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc), 0.5, new Random(1));
        Assert.Equal(2, gpus.Count);
        Assert.All(gpus, g => Assert.InRange(g.TemperatureC!.Value, 30, 100));
        var hw = SimulatedHardware.Controller(12, "SIM-SRV-12", DateTime.UtcNow, new Random(1));
        Assert.True(hw.Ok);
        Assert.Contains(hw.Components, c => c.Category == "PowerSupply");
        Assert.Contains(hw.Components, c => c.Category == "Drive");
        Assert.NotNull(hw.PowerWatts);
    }

    [Fact]
    public void Default_rules_cover_gpu_and_hardware()
    {
        var rules = DefaultRules.Create(DateTime.UtcNow);
        var util = Assert.Single(rules, r => r.Name == "GPU utilization high");
        Assert.Equal((95.0, 600), (util.Threshold!.Value, util.DurationSec));
        Assert.Equal(85, Assert.Single(rules, r => r.Name == "GPU temperature high").Threshold);
        Assert.Equal(90, Assert.Single(rules, r => r.Name == "GPU memory high").Threshold);
        Assert.Equal(StateKeys.GpuProblem, Assert.Single(rules, r => r.Name == "GPU missing or driver error").Key);
        Assert.Equal(StateKeys.HardwareComponent, Assert.Single(rules, r => r.Name == "Server hardware component not OK").Key);
    }
}
