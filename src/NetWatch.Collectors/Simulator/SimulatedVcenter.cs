using NetWatch.Collectors.Vmware;
using NetWatch.Core;

namespace NetWatch.Collectors.Simulator;

/// <summary>
/// A simulated vCenter: 4 ESXi hosts in one cluster, 3 datastores and the 40 simulated VMs (SIM-VM-01..40 with the
/// same IPs as their inventory devices, so their VMware alerts land on those devices). Produces filling datastores,
/// old and large snapshots, CPU-ready spikes, a VM without VMware Tools, DRS migrations and occasional host outages.
/// </summary>
public sealed class SimulatedVcenter
{
    private const int HostCount = 4;
    private const int VmCount = 40;

    private readonly Random _rng = new(2468);
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private readonly object _lock = new();
    private double _ssdUsed = 0.835;
    private long _nextEventKey = 5000;
    private int _hostOutagePolls;
    private int _outageHost = -1;

    private static string HostName(int i) => $"SIM-ESX-{i + 1:D2}";

    private static string VmName(int i) => $"SIM-VM-{i + 1:D2}";

    public VmwarePollData Poll(VmwarePollRequest request, DateTime now, double diurnal)
    {
        lock (_lock)
        {
            var data = new VmwarePollData { Ok = true, ApiType = "VirtualCenter", Version = "8.0.3", FullName = "VMware vCenter Server 8.0.3 build-24322831 (simulated)" };
            var events = new List<VmwareEventReading>();

            // ---- occasional host outage (HA restarts its VMs elsewhere)
            if (_hostOutagePolls == 0 && _rng.NextDouble() < 0.0008)
            {
                _outageHost = _rng.Next(HostCount);
                _hostOutagePolls = _rng.Next(3, 8);
                events.Add(Event(now, "HostConnectionLostEvent", Severity.Major, HostName(_outageHost), $"Host {HostName(_outageHost)} in SIM-Datacenter is not responding"));
                events.Add(Event(now, "DasHostFailedEvent", Severity.Critical, HostName(_outageHost), $"vSphere HA detected a possible host failure of host {HostName(_outageHost)} in cluster SIM-Cluster-01"));
            }
            else if (_hostOutagePolls > 0 && --_hostOutagePolls == 0)
            {
                events.Add(Event(now, "HostReconnectedEvent", Severity.Info, HostName(_outageHost), $"Host {HostName(_outageHost)} in SIM-Datacenter is responding again"));
                _outageHost = -1;
            }

            // ---- datastores: the SSD datastore fills slowly and is cleaned up at 97 %
            _ssdUsed += 0.00003;
            if (_ssdUsed > 0.97)
            {
                _ssdUsed = 0.78;
            }

            const long tb = 1L << 40;
            data.Datastores.Add(new DatastoreReading("datastore-11", "DS-SSD-01", "VMFS", 2 * tb, (long)(2 * tb * (1 - _ssdUsed)), 300L << 30, true, "normal", HostCount, VmCount / 2));
            data.Datastores.Add(new DatastoreReading("datastore-12", "DS-SAS-02", "VMFS", 8 * tb, (long)(8 * tb * 0.41), 1L << 40, true, "normal", HostCount, VmCount / 2));
            data.Datastores.Add(new DatastoreReading("datastore-13", "DS-ISO", "NFS", 500L << 30, 300L << 30, null, true, "normal", HostCount, 0));

            // ---- VMs
            var hostCpu = new double[HostCount];
            var hostMem = new double[HostCount];
            for (var i = 0; i < VmCount; i++)
            {
                var host = i % HostCount == _outageHost ? (i + 1) % HostCount : i % HostCount;
                var poweredOn = i is not (36 or 37);
                var cpus = i % 3 == 0 ? 4 : 2;
                var memMb = i % 3 == 0 ? 16384 : 8192;
                var cpuPct = poweredOn ? Math.Round(Math.Clamp(10 + (diurnal * 45) + (_rng.NextDouble() * 15) + (i % 7 * 3), 1, 100), 1) : 0;
                var maxMhz = cpus * 2600L;
                var usageMhz = (long)(maxMhz * cpuPct / 100);
                var memUsed = poweredOn ? (long)(memMb * (0.35 + (_rng.NextDouble() * 0.4))) : 0;
                hostCpu[host] += usageMhz;
                hostMem[host] += memUsed;
                var ds = i % 2 == 0 ? "datastore-11" : "datastore-12";
                data.Vms.Add(new VmReading(
                    $"vm-{1000 + i}", VmName(i), $"host-{100 + host}", poweredOn ? "poweredOn" : "poweredOff",
                    i % 5 == 4 ? "Red Hat Enterprise Linux 9 (64-bit)" : "Microsoft Windows Server 2019 (64-bit)",
                    poweredOn ? $"10.250.20.{10 + i}" : null, VmName(i).ToLowerInvariant() + ".sim.local",
                    !poweredOn ? "guestToolsNotRunning" : i == 11 ? "guestToolsNotRunning" : "guestToolsRunning",
                    i == 11 ? "guestToolsNotInstalled" : "guestToolsCurrent",
                    i == 11 ? "yellow" : "green", cpus, memMb, usageMhz, memUsed, i == 8 ? 512 : 0,
                    (60L + i) << 30, (40L + i) << 30, poweredOn && i != 11 ? 40 + (i * 1.3 % 45) : null,
                    [ds], poweredOn ? _startUtc.AddDays(-(i % 30) - 1) : null, poweredOn ? cpuPct : null));

                if (poweredOn && request.CollectCpuReady)
                {
                    // One VM on an over-committed host suffers from CPU ready during office hours.
                    var ready = i == 6 ? 4 + (diurnal * 10) + (_rng.NextDouble() * 2) : 0.3 + (_rng.NextDouble() * 2.5);
                    data.CpuReadyPct[$"vm-{1000 + i}"] = Math.Round(ready, 2);
                }
            }

            // ---- hosts
            for (var h = 0; h < HostCount; h++)
            {
                var down = h == _outageHost;
                const long coreMhz = 2600;
                const int cores = 32;
                const long mem = 512L << 30;
                data.Hosts.Add(new HostReading(
                    $"host-{100 + h}", HostName(h), "SIM-Cluster-01", down ? "notResponding" : "connected", down ? "unknown" : "poweredOn",
                    false, down ? "red" : "green", "Dell Inc.", "PowerEdge R760", "VMware ESXi 8.0.3 build-24280767",
                    cores, coreMhz * cores, down ? null : (long)hostCpu[h] + 1500, mem, down ? null : ((long)hostMem[h] << 20) + (24L << 30),
                    _startUtc.AddDays(-45 - h), 0, 0, []));
            }

            // ---- snapshots: a fresh one, an old large one, and a forgotten chain
            data.Snapshots.Add(new SnapshotReading("vm-1002", "snapshot-1", "Before patching", "KB5044281", _startUtc.AddDays(-2), 8L << 30, true));
            data.Snapshots.Add(new SnapshotReading("vm-1014", "snapshot-2", "pre-upgrade", "Application upgrade 4.2", _startUtc.AddDays(-12), 64L << 30, true));
            data.Snapshots.Add(new SnapshotReading("vm-1021", "snapshot-3", "baseline", null, _startUtc.AddDays(-30), 20L << 30, false));
            data.Snapshots.Add(new SnapshotReading("vm-1021", "snapshot-4", "after config", null, _startUtc.AddDays(-20), 15L << 30, true));

            // ---- alarms (vCenter's own view of the same problems)
            if (request.ImportAlarms)
            {
                data.Alarms = new List<AlarmReading>();
                if (_ssdUsed > 0.85)
                {
                    data.Alarms.Add(new AlarmReading("alarm-7.datastore-11", "alarm-7", "Datastore usage on disk", "datastore-11", "DS-SSD-01",
                        _ssdUsed > 0.95 ? "red" : "yellow", false, now));
                }

                if (_outageHost >= 0)
                {
                    data.Alarms.Add(new AlarmReading($"alarm-1.host-{100 + _outageHost}", "alarm-1", "Host connection and power state", $"host-{100 + _outageHost}",
                        HostName(_outageHost), "red", false, now));
                }
            }

            // ---- events: DRS moves VMs around regularly
            if (_rng.NextDouble() < 0.3)
            {
                var vm = _rng.Next(36);
                events.Add(Event(now, "DrsVmMigratedEvent", Severity.Info, VmName(vm),
                    $"{VmName(vm)} on {HostName(vm % HostCount)} in SIM-Datacenter is migrated to {HostName((vm + 1) % HostCount)} by DRS"));
            }

            if (request.ImportEvents)
            {
                if (request.EventBookmark is not { } bookmark)
                {
                    data.NewEventBookmark = (_nextEventKey, now);
                }
                else
                {
                    data.Events.AddRange(events.Where(e => e.Key > bookmark.Key));
                    data.NewEventBookmark = events.Count > 0 ? (events.Max(e => e.Key), now) : bookmark;
                }
            }

            return data;
        }
    }

    private VmwareEventReading Event(DateTime now, string type, Severity severity, string entity, string message) =>
        new(++_nextEventKey, now, type, severity, type.StartsWith("Drs", StringComparison.Ordinal) ? null : "vpxuser", entity, message);
}
