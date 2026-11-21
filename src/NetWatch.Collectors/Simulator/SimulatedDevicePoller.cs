using System.Collections.Concurrent;
using NetWatch.Collectors.Polling;
using NetWatch.Collectors.Servers;
using NetWatch.Core;

namespace NetWatch.Collectors.Simulator;

/// <summary>
/// Produces realistic fake poll results for devices tagged "simulated": business-hours traffic curves on
/// SNMP counters, CPU/memory, sensors, and random faults (outages, reboots, uplink flaps, fan failures,
/// CPU spikes) so the UI and alerting can be exercised without hardware.
/// </summary>
public sealed class SimulatedDevicePoller : IDevicePoller
{
    private static readonly DateTime SimEpoch = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ConcurrentDictionary<int, SimDevice> _devices = new();
    private readonly TimeProvider _time;
    private readonly SimulatedVcenter _vcenter = new();

    public SimulatedDevicePoller(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Fault probabilities per poll. Exposed for tests; defaults give a few incidents per hour across ~180 devices.</summary>
    internal double OutageProbability { get; set; } = 0.00015;
    internal double CpuSpikeProbability { get; set; } = 0.0004;
    internal double FlapProbability { get; set; } = 0.0002;
    internal double FanFailureProbability { get; set; } = 0.00005;

    private sealed class SimDevice
    {
        public required Random Rng;
        public required DeviceType Type;
        public required SimVendorSpec Vendor;
        public required List<SimPort> Ports;
        public DateTime BootUtc;
        public double BaseCpu;
        public double BaseMem;
        public double BaseRtt;
        public int DownPolls;
        public bool RebootAfterOutage;
        public int CpuSpikePolls;
        public int FanFailPolls;
        public DateTime LastPollUtc;
        public SimServer? Server;
    }

    private sealed class SimVolume
    {
        public required string Name;
        public required string? Label;
        public required string FileSystem;
        public required long Total;
        public double UsedFraction;
        public double GrowthPerPoll;
    }

    private sealed class SimServer
    {
        public required bool Linux;
        public required List<SimVolume> Volumes;
        public readonly Dictionary<string, int> ServiceDownPolls = new(StringComparer.OrdinalIgnoreCase);
        public long NextRecordId = 100_000;
        public bool PendingReboot;
        public DateTime LastPatchUtc;
        public ulong RxBytes;
        public ulong TxBytes;
    }

    private sealed class SimPort
    {
        public required SimPortSpec Spec;
        public bool Connected;
        public bool AdminUp = true;
        public double BaseUtil;
        public int FlapPolls;
        public ulong InOctets;
        public ulong OutOctets;
        public ulong InErrors;
        public ulong OutErrors;
    }

    public async Task<DevicePollResult> PollAsync(PollRequest request, CancellationToken ct)
    {
        var d = _devices.GetOrAdd(request.Device.Id, id => Create(id, request.Device.Type));

        // Simulate network round trip / SNMP latency.
        await Task.Delay(d.Rng.Next(5, 40), ct);
        var now = _time.GetUtcNow().UtcDateTime;
        var elapsed = d.LastPollUtc == default ? 60 : Math.Clamp((now - d.LastPollUtc).TotalSeconds, 1, 600);
        d.LastPollUtc = now;

        var result = new DevicePollResult { DeviceId = request.Device.Id, TimestampUtc = now };

        // ---- outage handling
        if (d.DownPolls == 0 && d.Rng.NextDouble() < OutageProbability)
        {
            d.DownPolls = d.Rng.Next(3, 9);
            d.RebootAfterOutage = d.Rng.NextDouble() < 0.6;
        }

        if (d.DownPolls > 0)
        {
            d.DownPolls--;
            if (d.DownPolls == 0 && d.RebootAfterOutage)
            {
                d.BootUtc = now.AddSeconds(-d.Rng.Next(20, 50));
                foreach (var p in d.Ports)
                {
                    p.InOctets = p.OutOctets = p.InErrors = p.OutErrors = 0;
                }
            }

            result.PingOk = false;
            result.LossPct = 100;
            result.SnmpOk = request.Snmp is null ? null : false;
            result.SnmpError = request.Snmp is null ? null : "Timeout";
            return result;
        }

        // ---- availability
        var diurnal = Diurnal(now);
        result.PingOk = true;
        result.RttMs = Math.Round(d.BaseRtt * (1 + (diurnal * 0.5)) + (d.Rng.NextDouble() * d.BaseRtt * 0.6), 2);
        result.LossPct = d.Rng.NextDouble() < 0.004 ? 33.3 : 0;

        if (request.Vmware is { } vmwareRequest && d.Type == DeviceType.VCenter)
        {
            result.Vmware = _vcenter.Poll(vmwareRequest, now, diurnal);
        }

        if (request.Snmp is null)
        {
            return result;
        }

        result.SnmpOk = true;
        result.UptimeSec = (long)(now - d.BootUtc).TotalSeconds;

        // ---- CPU / memory
        if (d.CpuSpikePolls == 0 && d.Rng.NextDouble() < CpuSpikeProbability)
        {
            d.CpuSpikePolls = d.Rng.Next(4, 12);
        }

        var spike = d.CpuSpikePolls > 0 ? 55 + (d.Rng.NextDouble() * 10) : 0;
        if (d.CpuSpikePolls > 0)
        {
            d.CpuSpikePolls--;
        }

        result.CpuPct = Math.Round(Math.Clamp(d.BaseCpu + (diurnal * 20) + spike + ((d.Rng.NextDouble() - 0.5) * 6), 1, 100), 1);
        result.MemPct = Math.Round(Math.Clamp(d.BaseMem + (diurnal * 5) + ((d.Rng.NextDouble() - 0.5) * 2), 5, 99), 1);

        // ---- interfaces
        foreach (var p in d.Ports)
        {
            if (p.FlapPolls == 0 && p.Spec.IsUplink && d.Rng.NextDouble() < FlapProbability)
            {
                p.FlapPolls = d.Rng.Next(1, 4);
            }

            var flapping = p.FlapPolls > 0;
            if (flapping)
            {
                p.FlapPolls--;
            }

            var operUp = p.AdminUp && p.Connected && !flapping;
            if (operUp)
            {
                var util = Math.Clamp(p.BaseUtil * (0.3 + (diurnal * 1.2)) * (0.85 + (d.Rng.NextDouble() * 0.3)), 0, 0.98);
                var inBits = p.Spec.SpeedBps * util;
                var outBits = p.Spec.SpeedBps * util * (0.4 + (d.Rng.NextDouble() * 0.4));
                p.InOctets += (ulong)(inBits * elapsed / 8);
                p.OutOctets += (ulong)(outBits * elapsed / 8);
                if (d.Rng.NextDouble() < 0.02)
                {
                    p.InErrors += (ulong)d.Rng.Next(1, 20);
                }
            }

            result.Interfaces.Add(new InterfaceCounters(p.Spec.IfIndex, p.InOctets, p.OutOctets, p.InErrors, p.OutErrors, 0, 0,
                operUp ? 1 : 2, p.AdminUp ? 1 : 2, HighCapacity: true));
        }

        if (request.RefreshInterfaces)
        {
            result.InterfaceMetadata = d.Ports
                .Select(p => new InterfaceMetadata(p.Spec.IfIndex, p.Spec.Name, p.Spec.Description,
                    p.Spec.IsUplink ? "Uplink" : (p.Connected ? "User port" : null), p.Spec.IfType, p.Spec.SpeedBps,
                    $"00:50:56:{request.Device.Id % 256:X2}:{p.Spec.IfIndex / 256:X2}:{p.Spec.IfIndex % 256:X2}"))
                .ToList();
        }

        if (request.RefreshInventory)
        {
            result.Inventory = new DeviceInventoryInfo(d.Vendor.Vendor, d.Vendor.Model, $"SIM{request.Device.Id:D6}", d.Vendor.Firmware,
                d.Vendor.SysObjectId, request.Device.Name, d.Vendor.SysDescription, "Simulated");
        }

        // ---- sensors (network devices and physical servers only)
        if (d.Type is DeviceType.Switch or DeviceType.Server)
        {
            if (d.FanFailPolls == 0 && d.Rng.NextDouble() < FanFailureProbability)
            {
                d.FanFailPolls = d.Rng.Next(20, 90);
            }

            var fanFailed = d.FanFailPolls > 0;
            if (fanFailed)
            {
                d.FanFailPolls--;
            }

            var temp = Math.Round(34 + (diurnal * 6) + (fanFailed ? 12 : 0) + (d.Rng.NextDouble() * 2), 1);
            result.Sensors.Add(new SensorReading(SensorKind.Temperature, "1", "Chassis inlet", temp, "C", temp > 55 ? SensorState.Warning : SensorState.Ok));
            result.Sensors.Add(new SensorReading(SensorKind.Fan, "1", "Fan tray 1", null, null, fanFailed ? SensorState.Critical : SensorState.Ok));
            result.Sensors.Add(new SensorReading(SensorKind.Fan, "2", "Fan tray 2", null, null, SensorState.Ok));
            result.Sensors.Add(new SensorReading(SensorKind.PowerSupply, "1", "Power supply 1", null, null, SensorState.Ok));
            result.Sensors.Add(new SensorReading(SensorKind.PowerSupply, "2", "Power supply 2", null, null, SensorState.Ok));
        }

        if (request.Server is { } serverRequest && d.Type is DeviceType.Server or DeviceType.VM)
        {
            result.Server = SimulateServer(d, serverRequest, now, diurnal, elapsed, result);
        }

        return result;
    }

    internal double ServiceFailureProbability { get; set; } = 0.0004;
    internal double WindowsErrorProbability { get; set; } = 0.01;

    private static readonly (int Id, string Provider, int Level, string Message)[] WindowsEvents =
    [
        (7031, "Service Control Manager", 2, "The Print Spooler service terminated unexpectedly. It has done this 1 time(s)."),
        (1000, "Application Error", 2, "Faulting application name: w3wp.exe, version: 10.0.20348.1, faulting module name: KERNELBASE.dll"),
        (10016, "DistributedCOM", 2, "The application-specific permission settings do not grant Local Activation permission for the COM Server application."),
        (36887, "Schannel", 2, "A fatal alert was received from the remote endpoint. The TLS protocol defined fatal alert code is 46."),
        (2004, "Microsoft-Windows-Resource-Exhaustion-Detector", 3, "Windows successfully diagnosed a low virtual memory condition."),
        (41, "Microsoft-Windows-Kernel-Power", 1, "The system has rebooted without cleanly shutting down first."),
    ];

    private static SimServer CreateServer(SimDevice d, bool linux)
    {
        var rng = d.Rng;
        List<SimVolume> volumes = linux
            ?
            [
                new SimVolume { Name = "/", Label = "/dev/mapper/vg0-root", FileSystem = "xfs", Total = 50L << 30, UsedFraction = 0.35 + (rng.NextDouble() * 0.3) },
                new SimVolume { Name = "/var", Label = "/dev/mapper/vg0-var", FileSystem = "xfs", Total = 100L << 30, UsedFraction = 0.4 + (rng.NextDouble() * 0.45) },
                new SimVolume { Name = "/data", Label = "/dev/sdb1", FileSystem = "ext4", Total = 1L << 40, UsedFraction = 0.2 + (rng.NextDouble() * 0.5) },
            ]
            :
            [
                new SimVolume { Name = "C:", Label = "System", FileSystem = "NTFS", Total = 120L << 30, UsedFraction = 0.4 + (rng.NextDouble() * 0.4) },
                new SimVolume { Name = "D:", Label = "Data", FileSystem = "NTFS", Total = 500L << 30, UsedFraction = 0.3 + (rng.NextDouble() * 0.5) },
            ];

        // About one server in six fills a disk slowly, so disk warnings appear within hours.
        if (rng.NextDouble() < 0.17)
        {
            var v = volumes[^1];
            v.UsedFraction = 0.83 + (rng.NextDouble() * 0.02);
            v.GrowthPerPoll = 0.00004;
        }

        return new SimServer
        {
            Linux = linux,
            Volumes = volumes,
            PendingReboot = rng.NextDouble() < 0.12,
            LastPatchUtc = DateTime.UtcNow.Date.AddDays(-rng.Next(3, 75)),
            RxBytes = (ulong)rng.NextInt64(1L << 30, 1L << 40),
            TxBytes = (ulong)rng.NextInt64(1L << 30, 1L << 40),
        };
    }

    private ServerPollData SimulateServer(SimDevice d, Servers.ServerPollRequest request, DateTime now, double diurnal, double elapsed, DevicePollResult result)
    {
        var linux = request.Credential?.Kind == CredentialKind.Ssh;
        var s = d.Server ??= CreateServer(d, linux);
        var data = new ServerPollData
        {
            Method = linux ? ServerCollectionMethod.Ssh : ServerCollectionMethod.WinRm,
            Ok = true,
            OsName = linux
                ? (d.Rng.Next(2) == 0 ? "Ubuntu 22.04.4 LTS" : "Red Hat Enterprise Linux 9.4 (Plow)")
                : d.Type == DeviceType.VM ? "Microsoft Windows Server 2019 Standard" : "Microsoft Windows Server 2022 Standard",
            OsVersion = linux ? "5.15.0-105-generic" : d.Type == DeviceType.VM ? "10.0.17763" : "10.0.20348",
            LastBootUtc = d.BootUtc,
            PendingReboot = s.PendingReboot,
            LastPatchUtc = s.LastPatchUtc,
            LastPatchId = linux ? null : "KB5044281",
            CpuPct = result.CpuPct,
            MemPct = result.MemPct,
            MemTotalBytes = d.Type == DeviceType.VM ? 16L << 30 : 128L << 30,
            CpuCount = d.Type == DeviceType.VM ? 4 : 32,
        };

        foreach (var v in s.Volumes)
        {
            v.UsedFraction += v.GrowthPerPoll;
            if (v.UsedFraction > 0.975)
            {
                v.UsedFraction = 0.6; // an administrator cleaned up
            }

            var free = (long)(v.Total * (1 - v.UsedFraction));
            data.Volumes.Add(new VolumeReading(v.Name, v.Label, v.FileSystem, v.Total, free));
        }

        foreach (var name in request.WatchedServices)
        {
            s.ServiceDownPolls.TryGetValue(name, out var down);
            if (down == 0 && d.Rng.NextDouble() < ServiceFailureProbability)
            {
                down = d.Rng.Next(2, 8);
            }

            if (down > 0)
            {
                down--;
            }

            s.ServiceDownPolls[name] = down;
            var state = down > 0 ? (linux ? ServiceState.Failed : ServiceState.Stopped) : ServiceState.Running;
            data.Services.Add(new ServiceReading(name, linux ? $"{name}.service" : DisplayName(name), state, linux ? null : "Auto"));
        }

        // Network throughput follows the server NIC traffic of the interface simulation.
        var bps = 2e8 * (0.2 + diurnal) * (0.7 + (d.Rng.NextDouble() * 0.6));
        if (linux)
        {
            s.RxBytes += (ulong)(bps * elapsed / 8);
            s.TxBytes += (ulong)(bps * 0.6 * elapsed / 8);
            data.NetRxBytes = s.RxBytes;
            data.NetTxBytes = s.TxBytes;
            var load = (result.CpuPct ?? 20) / 100.0 * data.CpuCount!.Value * (0.8 + (d.Rng.NextDouble() * 0.4));
            data.Load1 = Math.Round(load * 1.1, 2);
            data.Load5 = Math.Round(load, 2);
            data.Load15 = Math.Round(load * 0.9, 2);
        }
        else
        {
            data.NetInBps = bps;
            data.NetOutBps = bps * 0.6;
            if (request.ReadEventLogs)
            {
                var bookmarks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var log in new[] { "System", "Application" })
                {
                    if (!request.EventBookmarks.TryGetValue(log, out var last))
                    {
                        bookmarks[log] = s.NextRecordId;
                        continue;
                    }

                    if (d.Rng.NextDouble() < WindowsErrorProbability)
                    {
                        // Kernel-Power 41 (critical) is rare; the others are everyday errors.
                        var e = d.Rng.NextDouble() < 0.08 ? WindowsEvents[^1] : WindowsEvents[d.Rng.Next(WindowsEvents.Length - 1)];
                        s.NextRecordId = Math.Max(s.NextRecordId, last) + 1;
                        data.Events.Add(new WindowsEventReading(log, s.NextRecordId, now, e.Level, e.Id, e.Provider, e.Message));
                    }

                    bookmarks[log] = Math.Max(last, s.NextRecordId);
                }

                data.NewBookmarks = bookmarks;
            }
        }

        return data;
    }

    private static string DisplayName(string service) => service switch
    {
        "W3SVC" => "World Wide Web Publishing Service",
        "Spooler" => "Print Spooler",
        "WinRM" => "Windows Remote Management (WS-Management)",
        "EventLog" => "Windows Event Log",
        "MSSQLSERVER" => "SQL Server (MSSQLSERVER)",
        _ => service,
    };

    private static SimDevice Create(int id, DeviceType type)
    {
        var rng = new Random(id * 7919);
        var ports = SimulatedTopology.PortsFor(type).Select(spec => new SimPort
        {
            Spec = spec,
            Connected = spec.IsUplink || rng.NextDouble() < 0.7,
            AdminUp = spec.IsUplink || rng.NextDouble() > 0.05,
            BaseUtil = spec.IsUplink ? 0.15 + (rng.NextDouble() * 0.45) : rng.NextDouble() * 0.12,
            InOctets = (ulong)rng.NextInt64(0, 1L << 40),
            OutOctets = (ulong)rng.NextInt64(0, 1L << 40),
        }).ToList();

        // A handful of uplinks run hot so utilisation alerts appear during business hours.
        if (type == DeviceType.Switch && id % 17 == 0)
        {
            ports[^1].BaseUtil = 0.75;
        }

        return new SimDevice
        {
            Rng = rng,
            Type = type,
            Vendor = SimulatedTopology.VendorFor(type, id),
            Ports = ports,
            // A fixed reference date keeps uptimes growing across service restarts (no false "rebooted" alerts).
            BootUtc = SimEpoch.AddDays(-rng.Next(3, 400)).AddSeconds(-rng.Next(0, 86_400)),
            BaseCpu = type switch { DeviceType.Switch => 8 + rng.Next(0, 15), DeviceType.VM => 10 + rng.Next(0, 40), _ => 15 + rng.Next(0, 30) },
            BaseMem = type switch { DeviceType.Switch => 30 + rng.Next(0, 25), _ => 40 + rng.Next(0, 40) },
            BaseRtt = type switch { DeviceType.VM => 0.4, DeviceType.Server => 0.6, _ => 1.2 + (rng.NextDouble() * 2) },
        };
    }

    /// <summary>0..1 load factor following office hours in Pakistan Standard Time (UTC+5, no DST).</summary>
    internal static double Diurnal(DateTime utc)
    {
        var hour = (utc.Hour + 5 + (utc.Minute / 60.0)) % 24;
        var weekday = utc.AddHours(5).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        if (hour < 8 || hour > 18)
        {
            return 0.1;
        }

        var peak = Math.Sin(Math.PI * (hour - 8) / 10);
        return (weekday ? 1.0 : 0.3) * Math.Max(0.1, peak);
    }
}

/// <summary>Sends devices tagged "simulated" to the simulator and everything else to the real network poller.</summary>
public sealed class RoutingDevicePoller : IDevicePoller
{
    private readonly SimulatedDevicePoller _simulator;
    private readonly NetworkDevicePoller _network;

    public RoutingDevicePoller(SimulatedDevicePoller simulator, NetworkDevicePoller network)
    {
        _simulator = simulator;
        _network = network;
    }

    public Task<DevicePollResult> PollAsync(PollRequest request, CancellationToken ct) =>
        request.Device.IsSimulated ? _simulator.PollAsync(request, ct) : _network.PollAsync(request, ct);
}
