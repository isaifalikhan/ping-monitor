using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NetWatch.Core;
using static NetWatch.Collectors.Vmware.Vim;

namespace NetWatch.Collectors.Vmware;

/// <summary>
/// Collects VMware inventory and health from vCenter (or a standalone ESXi host) through the vSphere Web
/// Services API: hosts, VMs, datastores and snapshots in one property-collector query over a container view, CPU
/// ready from the performance manager, triggered alarms and new events. Read-only: the account only needs the
/// vCenter "Read-only" role.
/// </summary>
public sealed class VmwareCollector : IVmwareCollector
{
    private static readonly string[] HostProps =
    [
        "name", "parent", "overallStatus", "runtime.connectionState", "runtime.powerState", "runtime.inMaintenanceMode", "runtime.bootTime",
        "summary.hardware.vendor", "summary.hardware.model", "summary.hardware.cpuMhz", "summary.hardware.numCpuCores", "summary.hardware.memorySize",
        "summary.quickStats.overallCpuUsage", "summary.quickStats.overallMemoryUsage", "summary.config.product.fullName",
        "runtime.healthSystemRuntime.systemHealthInfo.numericSensorInfo",
    ];

    private static readonly string[] VmProps =
    [
        "name", "overallStatus", "runtime.host", "runtime.powerState", "runtime.bootTime", "runtime.maxCpuUsage",
        "summary.config.guestFullName", "summary.config.numCpu", "summary.config.memorySizeMB", "summary.config.template",
        "summary.quickStats.overallCpuUsage", "summary.quickStats.guestMemoryUsage", "summary.quickStats.balloonedMemory",
        "summary.storage.committed", "summary.storage.uncommitted",
        "guest.ipAddress", "guest.hostName", "guest.toolsRunningStatus", "guest.toolsVersionStatus2", "guest.disk",
        "datastore", "snapshot", "layoutEx.file", "layoutEx.snapshot", "layoutEx.disk",
    ];

    private static readonly string[] DatastoreProps =
    [
        "summary.name", "summary.type", "summary.capacity", "summary.freeSpace", "summary.uncommitted", "summary.accessible",
        "summary.maintenanceMode", "host", "vm",
    ];

    // Our own logins every poll would flood the event list.
    private static readonly HashSet<string> IgnoredEvents = new(StringComparer.Ordinal)
    {
        "UserLoginSessionEvent", "UserLogoutSessionEvent", "BadUsernameSessionEvent", "SessionTerminatedEvent",
    };

    // cpu.ready.summation counter id per endpoint (stable for the life of a vCenter).
    private static readonly ConcurrentDictionary<string, int> ReadyCounterIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<VmwareCollector> _logger;

    public VmwareCollector(ILogger<VmwareCollector> logger)
    {
        _logger = logger;
    }

    public async Task<VmwarePollData> CollectAsync(VmwarePollRequest request, CancellationToken ct)
    {
        var data = new VmwarePollData();
        using var client = new VimClient(request.Host, request.PinnedThumbprint, request.TrustOnFirstUse, request.Timeout);
        var serviceInstance = new MoRef("ServiceInstance", "ServiceInstance");
        var loggedIn = false;
        MoRef? sessionManager = null;
        try
        {
            var sc = (await client.InvokeAsync("RetrieveServiceContent", serviceInstance, [], ct)).Element(Ns + "returnval")!;
            var about = sc.Element(Ns + "about");
            data.ApiType = about?.Child("apiType");
            data.Version = about?.Child("version");
            data.FullName = about?.Child("fullName");
            var pc = MoRef.From(sc.Element(Ns + "propertyCollector"))!.Value;
            var root = MoRef.From(sc.Element(Ns + "rootFolder"))!.Value;
            var viewManager = MoRef.From(sc.Element(Ns + "viewManager"))!.Value;
            sessionManager = MoRef.From(sc.Element(Ns + "sessionManager"));
            var perfManager = MoRef.From(sc.Element(Ns + "perfManager"));
            var eventManager = MoRef.From(sc.Element(Ns + "eventManager"));

            await client.InvokeAsync("Login", sessionManager!.Value,
                [E("userName", UserName(request.Credential)), E("password", request.Credential.Secret.Password ?? string.Empty)], ct);
            loggedIn = true;

            // ---- inventory: one container view over everything
            var view = MoRef.From((await client.InvokeAsync("CreateContainerView", viewManager,
            [
                root.ToElement("container"),
                E("type", "HostSystem"), E("type", "VirtualMachine"), E("type", "Datastore"), E("type", "ClusterComputeResource"),
                E("recursive", "true"),
            ], ct)).Element(Ns + "returnval"))!.Value;

            var objects = await RetrieveAsync(client, pc,
            [
                E("propSet", E("type", "HostSystem"), HostProps.Select(p => E("pathSet", p))),
                E("propSet", E("type", "VirtualMachine"), VmProps.Select(p => E("pathSet", p))),
                E("propSet", E("type", "Datastore"), DatastoreProps.Select(p => E("pathSet", p))),
                E("propSet", E("type", "ClusterComputeResource"), E("pathSet", "name")),
                E("objectSet", view.ToElement("obj"), E("skip", "true"),
                    Typed("selectSet", "TraversalSpec", E("name", "view"), E("type", "ContainerView"), E("path", "view"), E("skip", "false"))),
            ], ct);
            await TryAsync(() => client.InvokeAsync("DestroyView", view, [], ct));

            VimParser.ParseInventory(objects, data);

            // ---- CPU ready (performance manager, realtime 20-second samples)
            if (request.CollectCpuReady && perfManager is { } pm)
            {
                await TryAsync(async () => await CollectCpuReadyAsync(client, pc, pm, request.Host, data, ct));
            }

            // ---- triggered alarms
            if (request.ImportAlarms)
            {
                await TryAsync(async () => data.Alarms = await CollectAlarmsAsync(client, pc, root, data, ct));
            }

            // ---- events
            if (request.ImportEvents && eventManager is { } em)
            {
                await TryAsync(async () => await CollectEventsAsync(client, pc, em, request, data, ct));
            }

            data.Ok = true;
        }
        catch (VimFaultException ex)
        {
            data.Error = ex.FaultType switch
            {
                "InvalidLogin" => "vCenter login failed (check user name and password; use user@vsphere.local or DOMAIN\\user).",
                "NoPermission" => "The vCenter account lacks permission (assign the Read-only role at the vCenter root, propagate to children).",
                _ => ex.Message,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            data.Error = ex is TaskCanceledException or OperationCanceledException ? "vCenter did not answer in time." : $"vCenter: {ex.Message}";
        }
        finally
        {
            if (loggedIn && sessionManager is { } sm)
            {
                await TryAsync(() => client.InvokeAsync("Logout", sm, [], CancellationToken.None));
            }

            data.NewThumbprint = client.NewlyPinnedThumbprint;
        }

        return data;

        async Task TryAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is VimFaultException or HttpRequestException or System.Xml.XmlException or FormatException ||
                                       (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                _logger.LogDebug(ex, "Optional vSphere query failed for {Host}", request.Host);
            }
        }
    }

    /// <summary>"user@domain" or "DOMAIN\user" as vCenter expects it.</summary>
    internal static string UserName(Servers.ManagementCredential c)
    {
        var user = c.Secret.Username ?? string.Empty;
        return user.Contains('@') || user.Contains('\\') || string.IsNullOrWhiteSpace(c.Secret.Domain) ? user : $"{user}@{c.Secret.Domain}";
    }

    /// <summary>RetrievePropertiesEx with paging (ContinueRetrievePropertiesEx). Returns all ObjectContent elements.</summary>
    private static async Task<List<XElement>> RetrieveAsync(VimClient client, MoRef pc, XElement[] specContent, CancellationToken ct)
    {
        var result = new List<XElement>();
        var response = await client.InvokeAsync("RetrievePropertiesEx", pc, [E("specSet", specContent), E("options", E("maxObjects", "1000"))], ct);
        var ret = response.Element(Ns + "returnval");
        while (ret is not null)
        {
            result.AddRange(ret.Children("objects"));
            var token = ret.Child("token");
            if (string.IsNullOrEmpty(token))
            {
                break;
            }

            ret = (await client.InvokeAsync("ContinueRetrievePropertiesEx", pc, [E("token", token)], ct)).Element(Ns + "returnval");
        }

        return result;
    }

    private static async Task CollectCpuReadyAsync(VimClient client, MoRef pc, MoRef pm, string host, VmwarePollData data, CancellationToken ct)
    {
        if (!ReadyCounterIds.TryGetValue(host, out var counterId))
        {
            var counters = await RetrieveAsync(client, pc,
            [
                E("propSet", E("type", "PerformanceManager"), E("pathSet", "perfCounter")),
                E("objectSet", pm.ToElement("obj")),
            ], ct);
            counterId = VimParser.FindCounter(counters, "cpu", "ready", "summation") ?? -1;
            ReadyCounterIds[host] = counterId;
        }

        var vms = data.Vms.Where(v => v.PoweredOn).ToList();
        if (counterId < 0 || vms.Count == 0)
        {
            return;
        }

        foreach (var chunk in vms.Chunk(200))
        {
            var specs = chunk.Select(v => E("querySpec",
                new MoRef("VirtualMachine", v.MoRef).ToElement("entity"),
                E("maxSample", "1"),
                E("metricId", E("counterId", counterId.ToString(CultureInfo.InvariantCulture)), E("instance", string.Empty)),
                E("intervalId", "20")));
            var response = await client.InvokeAsync("QueryPerf", pm, specs, ct);
            foreach (var (vm, readyMs) in VimParser.ParsePerfSingleValues(response))
            {
                var cpus = data.Vms.FirstOrDefault(v => v.MoRef == vm)?.NumCpu ?? 1;
                data.CpuReadyPct[vm] = VimParser.ReadyPercent(readyMs, 20, cpus);
            }
        }
    }

    private static async Task<List<AlarmReading>> CollectAlarmsAsync(VimClient client, MoRef pc, MoRef root, VmwarePollData data, CancellationToken ct)
    {
        var folder = await RetrieveAsync(client, pc,
        [
            E("propSet", E("type", "Folder"), E("pathSet", "triggeredAlarmState")),
            E("objectSet", root.ToElement("obj")),
        ], ct);
        var states = VimParser.ParseAlarmStates(folder);
        if (states.Count == 0)
        {
            return new List<AlarmReading>();
        }

        var alarmRefs = states.Select(s => s.Alarm).Distinct().ToList();
        var alarms = await RetrieveAsync(client, pc,
        [
            E("propSet", E("type", "Alarm"), E("pathSet", "info.name")),
            .. alarmRefs.Select(a => E("objectSet", a.ToElement("obj"))),
        ], ct);
        var names = alarms.ToDictionary(o => MoRef.From(o.Element(Ns + "obj"))!.Value.Value, o => VimParser.Props(o).GetValueOrDefault("info.name")?.Value ?? "Alarm");
        var entityNames = VimParser.EntityNames(data);

        return states.Select(s => new AlarmReading(
            s.Key, s.Alarm.Value, names.GetValueOrDefault(s.Alarm.Value, s.Alarm.Value), s.Entity.Value,
            entityNames.GetValueOrDefault(s.Entity.Value), s.Status, s.Acknowledged, s.Time)).ToList();
    }

    private static async Task CollectEventsAsync(VimClient client, MoRef pc, MoRef em, VmwarePollRequest request, VmwarePollData data, CancellationToken ct)
    {
        if (request.EventBookmark is not { } bookmark)
        {
            // First contact: remember the newest event; history is not imported.
            var latest = await RetrieveAsync(client, pc, [E("propSet", E("type", "EventManager"), E("pathSet", "latestEvent")), E("objectSet", em.ToElement("obj"))], ct);
            var ev = latest.Select(o => VimParser.Props(o).GetValueOrDefault("latestEvent")).FirstOrDefault(v => v is not null);
            data.NewEventBookmark = ev is null ? (0, DateTime.UtcNow) : (VimParser.Long(ev.Child("key")) ?? 0, VimParser.Date(ev.Child("createdTime")) ?? DateTime.UtcNow);
            return;
        }

        // Overlap by a few minutes (clock differences); duplicates are filtered by key.
        var begin = bookmark.TimeUtc.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var collector = MoRef.From((await client.InvokeAsync("CreateCollectorForEvents", em,
            [E("filter", E("time", E("beginTime", begin)))], ct)).Element(Ns + "returnval"))!.Value;
        try
        {
            var newest = bookmark;
            while (data.Events.Count < request.MaxEvents)
            {
                var batch = (await client.InvokeAsync("ReadNextEvents", collector, [E("maxCount", "200")], ct)).Children("returnval").ToList();
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var e in batch)
                {
                    var parsed = VimParser.ParseEvent(e);
                    if (parsed is null || parsed.Key <= bookmark.Key)
                    {
                        continue;
                    }

                    if (parsed.Key > newest.Key)
                    {
                        newest = (parsed.Key, parsed.CreatedUtc);
                    }

                    if (!IgnoredEvents.Contains(parsed.EventType) && data.Events.Count < request.MaxEvents)
                    {
                        data.Events.Add(parsed);
                    }
                }
            }

            data.NewEventBookmark = newest;
        }
        finally
        {
            try
            {
                await client.InvokeAsync("DestroyCollector", collector, [], CancellationToken.None);
            }
            catch (Exception ex) when (ex is VimFaultException or HttpRequestException)
            {
                // The collector expires with the session anyway.
            }
        }
    }
}

/// <summary>Parsing of vim25 property-collector results. Pure functions (unit tested with captured XML).</summary>
public static class VimParser
{
    public static long? Long(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static DateTime? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    /// <summary>propSet name → val element of one ObjectContent.</summary>
    public static Dictionary<string, XElement> Props(XElement objectContent) =>
        objectContent.Children("propSet")
            .Where(p => p.Child("name") is not null && p.Element(Ns + "val") is not null)
            .GroupBy(p => p.Child("name")!)
            .ToDictionary(g => g.Key, g => g.First().Element(Ns + "val")!, StringComparer.Ordinal);

    private static string? S(Dictionary<string, XElement> p, string name) => p.TryGetValue(name, out var v) ? v.Value.Trim() : null;

    private static long? L(Dictionary<string, XElement> p, string name) => Long(S(p, name));

    public static void ParseInventory(IEnumerable<XElement> objects, VmwarePollData data)
    {
        var clusters = new Dictionary<string, string>(StringComparer.Ordinal);
        var hosts = new List<(string MoRef, Dictionary<string, XElement> P)>();
        var vms = new List<(string MoRef, Dictionary<string, XElement> P)>();
        var datastores = new List<(string MoRef, Dictionary<string, XElement> P)>();
        foreach (var o in objects)
        {
            if (MoRef.From(o.Element(Ns + "obj")) is not { } obj)
            {
                continue;
            }

            var props = Props(o);
            switch (obj.Type)
            {
                case "ClusterComputeResource":
                    clusters[obj.Value] = S(props, "name") ?? obj.Value;
                    break;
                case "HostSystem":
                    hosts.Add((obj.Value, props));
                    break;
                case "VirtualMachine":
                    vms.Add((obj.Value, props));
                    break;
                case "Datastore":
                    datastores.Add((obj.Value, props));
                    break;
            }
        }

        foreach (var (moref, p) in hosts)
        {
            var parent = p.TryGetValue("parent", out var par) ? MoRef.From(par) : null;
            var sensors = p.TryGetValue("runtime.healthSystemRuntime.systemHealthInfo.numericSensorInfo", out var s)
                ? s.Children("HostNumericSensorInfo").Select(x => (Name: x.Child("name") ?? "sensor", State: x.Element(Ns + "healthState")?.Child("key") ?? "unknown")).ToList()
                : [];
            var mhz = L(p, "summary.hardware.cpuMhz");
            var cores = L(p, "summary.hardware.numCpuCores");
            data.Hosts.Add(new HostReading(
                moref,
                S(p, "name") ?? moref,
                parent is { Type: "ClusterComputeResource" } c ? clusters.GetValueOrDefault(c.Value) : null,
                S(p, "runtime.connectionState") ?? "unknown",
                S(p, "runtime.powerState"),
                S(p, "runtime.inMaintenanceMode") == "true",
                S(p, "overallStatus"),
                S(p, "summary.hardware.vendor"),
                S(p, "summary.hardware.model"),
                S(p, "summary.config.product.fullName"),
                (int?)cores,
                mhz is not null && cores is not null ? mhz * cores : null,
                L(p, "summary.quickStats.overallCpuUsage"),
                L(p, "summary.hardware.memorySize"),
                L(p, "summary.quickStats.overallMemoryUsage") is { } memMb ? memMb * 1024 * 1024 : null,
                Date(S(p, "runtime.bootTime")),
                sensors.Count(x => x.State == "yellow"),
                sensors.Count(x => x.State == "red"),
                sensors.Where(x => x.State is "red" or "yellow").Select(x => $"{x.Name} ({x.State})").ToList()));
        }

        foreach (var (moref, p) in vms)
        {
            if (S(p, "summary.config.template") == "true")
            {
                continue;
            }

            var disks = p.TryGetValue("guest.disk", out var gd) ? gd.Children("GuestDiskInfo").ToList() : [];
            double? diskMax = disks
                .Select(d => (Cap: Long(d.Child("capacity")) ?? 0, Free: Long(d.Child("freeSpace")) ?? 0))
                .Where(d => d.Cap > 0)
                .Select(d => (double?)Math.Round(100.0 * (d.Cap - d.Free) / d.Cap, 1))
                .DefaultIfEmpty()
                .Max();
            var maxCpu = L(p, "runtime.maxCpuUsage");
            var cpu = L(p, "summary.quickStats.overallCpuUsage");
            var datastoreRefs = p.TryGetValue("datastore", out var dsv) ? dsv.Children("ManagedObjectReference").Select(x => x.Value.Trim()).ToList() : [];
            var reading = new VmReading(
                moref,
                S(p, "name") ?? moref,
                p.TryGetValue("runtime.host", out var h) ? MoRef.From(h)?.Value : null,
                S(p, "runtime.powerState") ?? "unknown",
                S(p, "summary.config.guestFullName"),
                S(p, "guest.ipAddress"),
                S(p, "guest.hostName"),
                S(p, "guest.toolsRunningStatus"),
                S(p, "guest.toolsVersionStatus2"),
                S(p, "overallStatus"),
                (int?)L(p, "summary.config.numCpu"),
                (int?)L(p, "summary.config.memorySizeMB"),
                cpu,
                L(p, "summary.quickStats.guestMemoryUsage"),
                L(p, "summary.quickStats.balloonedMemory"),
                L(p, "summary.storage.committed"),
                L(p, "summary.storage.uncommitted"),
                diskMax,
                datastoreRefs,
                Date(S(p, "runtime.bootTime")),
                maxCpu is > 0 && cpu is not null ? Math.Round(Math.Clamp(100.0 * cpu.Value / maxCpu.Value, 0, 100), 1) : null);
            data.Vms.Add(reading);
            data.Snapshots.AddRange(ParseSnapshots(moref, p));
        }

        foreach (var (moref, p) in datastores)
        {
            data.Datastores.Add(new DatastoreReading(
                moref,
                S(p, "summary.name") ?? moref,
                S(p, "summary.type"),
                L(p, "summary.capacity") ?? 0,
                L(p, "summary.freeSpace") ?? 0,
                L(p, "summary.uncommitted"),
                S(p, "summary.accessible") != "false",
                S(p, "summary.maintenanceMode"),
                p.TryGetValue("host", out var hm) ? hm.Children("DatastoreHostMount").Count() : 0,
                p.TryGetValue("vm", out var vmv) ? vmv.Children("ManagedObjectReference").Count() : 0));
        }
    }


    /// <summary>Snapshots of one VM from the "snapshot" tree, with approximate sizes from "layoutEx".</summary>
    public static List<SnapshotReading> ParseSnapshots(string vmMoRef, Dictionary<string, XElement> p)
    {
        var result = new List<SnapshotReading>();
        if (!p.TryGetValue("snapshot", out var info))
        {
            return result;
        }

        var current = info.Element(Ns + "currentSnapshot")?.Value.Trim();
        var files = p.TryGetValue("layoutEx.file", out var lf)
            ? lf.Children("VirtualMachineFileLayoutExFileInfo").ToDictionary(f => Long(f.Child("key")) ?? -1, f => Long(f.Child("size")) ?? 0)
            : new Dictionary<long, long>();
        var layouts = p.TryGetValue("layoutEx.snapshot", out var ls)
            ? ls.Children("VirtualMachineFileLayoutExSnapshotLayout").ToDictionary(s => s.Element(Ns + "key")?.Value.Trim() ?? string.Empty, s => s)
            : new Dictionary<string, XElement>();
        var currentDisks = p.TryGetValue("layoutEx.disk", out var ld) ? DiskChains(ld.Children("VirtualMachineFileLayoutExDiskLayout")) : new HashSet<long>();

        void Walk(XElement tree, HashSet<long>? nextPointFiles)
        {
            var moref = tree.Element(Ns + "snapshot")?.Value.Trim() ?? string.Empty;
            var children = tree.Children("childSnapshotList").ToList();
            long? size = null;
            if (layouts.TryGetValue(moref, out var layout))
            {
                // Own files: memory/state file plus the delta disks created after this point (in the child's chain, not in ours).
                var own = DiskChains(layout.Children("disk"));
                var next = children.Count > 0 && layouts.TryGetValue(children[0].Element(Ns + "snapshot")?.Value.Trim() ?? string.Empty, out var childLayout)
                    ? DiskChains(childLayout.Children("disk"))
                    : nextPointFiles ?? currentDisks;
                var keys = next.Where(k => !own.Contains(k)).ToList();
                foreach (var k in new[] { Long(layout.Child("dataKey")), Long(layout.Child("memoryKey")) })
                {
                    if (k is { } key and >= 0)
                    {
                        keys.Add(key);
                    }
                }

                size = keys.Sum(k => files.GetValueOrDefault(k));
            }

            result.Add(new SnapshotReading(
                vmMoRef,
                moref,
                tree.Child("name") ?? "snapshot",
                string.IsNullOrWhiteSpace(tree.Child("description")) ? null : tree.Child("description"),
                Date(tree.Child("createTime")) ?? DateTime.UtcNow,
                size,
                moref == current));
            foreach (var child in children)
            {
                Walk(child, null);
            }
        }

        foreach (var rootTree in info.Children("rootSnapshotList"))
        {
            Walk(rootTree, null);
        }

        return result;
    }

    private static HashSet<long> DiskChains(IEnumerable<XElement> disks) =>
        disks.SelectMany(d => d.Children("chain")).SelectMany(c => c.Children("fileKey")).Select(k => Long(k.Value) ?? -1).Where(k => k >= 0).ToHashSet();

    /// <summary>Counter id of group.name.rollup (e.g. cpu.ready.summation) from the perfCounter property.</summary>
    public static int? FindCounter(IEnumerable<XElement> objects, string group, string name, string rollup)
    {
        foreach (var o in objects)
        {
            if (!Props(o).TryGetValue("perfCounter", out var counters))
            {
                continue;
            }

            foreach (var c in counters.Children("PerfCounterInfo"))
            {
                if (c.Element(Ns + "groupInfo")?.Child("key") == group && c.Element(Ns + "nameInfo")?.Child("key") == name && c.Child("rollupType") == rollup)
                {
                    return (int?)Long(c.Child("key"));
                }
            }
        }

        return null;
    }

    /// <summary>(entity MoRef, latest value) from a QueryPerf response with one counter per entity.</summary>
    public static IEnumerable<(string MoRef, long Value)> ParsePerfSingleValues(XElement queryPerfResponse)
    {
        foreach (var r in queryPerfResponse.Children("returnval"))
        {
            var entity = MoRef.From(r.Element(Ns + "entity"))?.Value;
            var series = r.Children("value").FirstOrDefault();
            var last = series?.Children("value").LastOrDefault();
            if (entity is not null && Long(last?.Value) is { } v && v >= 0)
            {
                yield return (entity, v);
            }
        }
    }

    /// <summary>CPU ready % per vCPU from a summation counter (ms of ready time in the interval).</summary>
    public static double ReadyPercent(long readyMs, int intervalSec, int vcpus) =>
        Math.Round(Math.Clamp(readyMs / (intervalSec * 1000.0) * 100.0 / Math.Max(1, vcpus), 0, 100), 2);

    public static List<(string Key, MoRef Entity, MoRef Alarm, string Status, bool Acknowledged, DateTime Time)> ParseAlarmStates(IEnumerable<XElement> objects)
    {
        var result = new List<(string, MoRef, MoRef, string, bool, DateTime)>();
        foreach (var o in objects)
        {
            if (!Props(o).TryGetValue("triggeredAlarmState", out var states))
            {
                continue;
            }

            foreach (var s in states.Children("AlarmState"))
            {
                var status = s.Child("overallStatus") ?? "gray";
                if (status is not ("red" or "yellow") || MoRef.From(s.Element(Ns + "entity")) is not { } entity || MoRef.From(s.Element(Ns + "alarm")) is not { } alarm)
                {
                    continue;
                }

                result.Add((s.Child("key") ?? $"{alarm.Value}.{entity.Value}", entity, alarm, status, s.Child("acknowledged") == "true", Date(s.Child("time")) ?? DateTime.UtcNow));
            }
        }

        return result;
    }

    /// <summary>MoRef → display name for all collected entities.</summary>
    public static Dictionary<string, string> EntityNames(VmwarePollData data)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in data.Hosts)
        {
            names[h.MoRef] = h.Name;
        }

        foreach (var v in data.Vms)
        {
            names[v.MoRef] = v.Name;
        }

        foreach (var d in data.Datastores)
        {
            names[d.MoRef] = d.Name;
        }

        return names;
    }

    private static readonly Dictionary<string, Severity> EventSeverities = new(StringComparer.Ordinal)
    {
        ["HostConnectionLostEvent"] = Severity.Major,
        ["HostDisconnectedEvent"] = Severity.Major,
        ["HostShutdownEvent"] = Severity.Major,
        ["DasHostFailedEvent"] = Severity.Critical,
        ["DasHostIsolatedEvent"] = Severity.Critical,
        ["DasClusterIsolatedEvent"] = Severity.Critical,
        ["VmFailoverFailed"] = Severity.Critical,
        ["VmDasBeingResetEvent"] = Severity.Major,
        ["VmDasResetFailedEvent"] = Severity.Major,
        ["DasVmPoweredOffEvent"] = Severity.Major,
        ["VmPoweredOffEvent"] = Severity.Warning,
        ["VmGuestShutdownEvent"] = Severity.Info,
        ["VmPoweredOnEvent"] = Severity.Info,
        ["VmMigratedEvent"] = Severity.Info,
        ["DrsVmMigratedEvent"] = Severity.Info,
        ["VmRemovedEvent"] = Severity.Warning,
        ["DatastoreRemovedOnHostEvent"] = Severity.Major,
        ["HostEnteredMaintenanceModeEvent"] = Severity.Info,
        ["HostExitMaintenanceModeEvent"] = Severity.Info,
        ["AlarmStatusChangedEvent"] = Severity.Info,
    };

    /// <summary>Parses one vim25 Event (any subtype). EventEx/ExtendedEvent carry their own type id and severity.</summary>
    public static VmwareEventReading? ParseEvent(XElement e)
    {
        if (Long(e.Child("key")) is not { } key)
        {
            return null;
        }

        var type = e.XsiType() ?? "Event";
        var severity = EventSeverities.TryGetValue(type, out var known) ? known : Severity.Info;
        if (type is "EventEx" or "ExtendedEvent")
        {
            type = e.Child("eventTypeId") ?? type;
            severity = e.Child("severity") switch
            {
                "error" => Severity.Major,
                "warning" => Severity.Warning,
                _ => severity,
            };
        }
        else if (!EventSeverities.ContainsKey(type) && type.Contains("Fail", StringComparison.Ordinal))
        {
            severity = Severity.Major;
        }

        var entity = e.Element(Ns + "vm")?.Child("name") ?? e.Element(Ns + "host")?.Child("name") ?? e.Element(Ns + "ds")?.Child("name") ?? e.Element(Ns + "computeResource")?.Child("name");
        var message = e.Child("fullFormattedMessage")?.Trim();
        return new VmwareEventReading(
            key,
            Date(e.Child("createdTime")) ?? DateTime.UtcNow,
            type,
            severity,
            string.IsNullOrWhiteSpace(e.Child("userName")) ? null : e.Child("userName"),
            entity,
            string.IsNullOrEmpty(message) ? type : message.Length > 4000 ? message[..4000] : message);
    }
}
