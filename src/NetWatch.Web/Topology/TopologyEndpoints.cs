using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Live;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Alerts;
using NetWatch.Data.Config;
using NetWatch.Web.Api;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Topology;

/// <summary>A discovered physical link (LLDP/CDP) with its live state.</summary>
public sealed record TopologyLinkDto(
    int Id, int DeviceId, string LocalPort, int? LocalInterfaceId, int? RemoteDeviceId, string? RemoteName, string? RemotePort, string Protocol,
    int? OperStatus, double? UtilPct, long? SpeedBps, DateTime LastSeenUtc);

public sealed record TopologyGraphNode(int Id, string Name, string IpAddress, DeviceType Type, DeviceStatus Status, Severity? WorstAlert, int? ParentDeviceId, bool Monitored);

public sealed record TopologyGraphDto(IReadOnlyList<TopologyGraphNode> Nodes, IReadOnlyList<TopologyLinkDto> Links, IReadOnlyList<string> Unmatched);

/// <summary>A suggested alert dependency: <c>DeviceId</c> sits behind <c>SuggestedParentId</c> as seen from the core.</summary>
public sealed record DependencySuggestion(int DeviceId, string DeviceName, int? CurrentParentId, string? CurrentParentName, int SuggestedParentId, string SuggestedParentName, string Via);

public sealed record ApplySuggestionsRequest(IReadOnlyList<int> DeviceIds);

/// <summary>Physical topology from LLDP/CDP and parent-dependency suggestions derived from it.</summary>
public static class TopologyEndpoints
{
    public static void MapTopologyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/topology/links", LinksAsync).RequireAuthorization(Policies.Viewer);
        app.MapGet("/api/topology/suggestions", SuggestionsAsync).RequireAuthorization(Policies.Viewer);
        app.MapPost("/api/topology/suggestions/apply", ApplyAsync).RequireAuthorization(Policies.Admin);
    }

    private static async Task<IResult> LinksAsync(NetWatchDbContext db, LiveStateCache live, IActiveAlertIndex alerts, InventoryCache inventory, IDependencyStatus dependencies, CancellationToken ct)
    {
        var links = await db.NeighborLinks.AsNoTracking().ToListAsync(ct);
        var devices = await db.Devices.AsNoTracking().Include(d => d.Tags).Where(d => d.IsEnabled).ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().ToListAsync(ct);
        var paths = DeviceStatusHelper.GroupPaths(groups);
        var now = DateTime.UtcNow;
        var involved = links.Select(l => l.DeviceId).Concat(links.Where(l => l.RemoteDeviceId is not null).Select(l => l.RemoteDeviceId!.Value)).ToHashSet();
        var nodes = devices.Where(d => involved.Contains(d.Id)).Select(d =>
        {
            var item = DeviceEndpoints.ToListItem(d, paths, live, alerts, inventory, dependencies, now);
            return new TopologyGraphNode(d.Id, d.Name, d.IpAddress, d.Type, item.Status, item.WorstAlert, d.ParentDeviceId, true);
        }).ToList();
        var ids = nodes.Select(n => n.Id).ToHashSet();

        var interfaceIds = links.Where(l => l.LocalInterfaceId is not null).Select(l => l.LocalInterfaceId!.Value).ToList();
        var speeds = await db.Interfaces.AsNoTracking().Where(i => interfaceIds.Contains(i.Id)).Select(i => new { i.Id, i.SpeedBps, i.OperStatus }).ToDictionaryAsync(i => i.Id, ct);

        // A link seen from both ends (A→B and B→A) is drawn once, from the lower device ID.
        var drawn = new HashSet<(int, int, string)>();
        var result = new List<TopologyLinkDto>();
        foreach (var l in links.Where(l => ids.Contains(l.DeviceId)).OrderBy(l => l.DeviceId))
        {
            if (l.RemoteDeviceId is { } r && ids.Contains(r))
            {
                var key = (Math.Min(l.DeviceId, r), Math.Max(l.DeviceId, r), l.DeviceId < r ? l.LocalPort : l.RemotePort ?? string.Empty);
                if (!drawn.Add(key) || (l.DeviceId > r && links.Any(o => o.DeviceId == r && o.RemoteDeviceId == l.DeviceId)))
                {
                    continue;
                }
            }

            var liveIf = l.LocalInterfaceId is { } lid ? live.GetInterface(lid) : null;
            var s = l.LocalInterfaceId is { } sid ? speeds.GetValueOrDefault(sid) : null;
            result.Add(new TopologyLinkDto(l.Id, l.DeviceId, l.LocalPort, l.LocalInterfaceId, l.RemoteDeviceId is { } rd && ids.Contains(rd) ? rd : null, l.RemoteName, l.RemotePort,
                l.Protocol, liveIf?.OperStatus ?? s?.OperStatus, liveIf is null ? null : Math.Max(liveIf.InUtilPct ?? 0, liveIf.OutUtilPct ?? 0), s?.SpeedBps, l.LastSeenUtc));
        }

        var unmatched = links.Where(l => l.RemoteDeviceId is null && l.RemoteName is not null).Select(l => l.RemoteName!).Distinct(StringComparer.OrdinalIgnoreCase).Order().Take(200).ToList();
        return Results.Ok(new TopologyGraphDto(nodes, result, unmatched));
    }

    private static async Task<IResult> SuggestionsAsync(NetWatchDbContext db, CancellationToken ct) => Results.Ok(await ComputeAsync(db, ct));

    private static async Task<IResult> ApplyAsync(ApplySuggestionsRequest request, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var wanted = request.DeviceIds?.ToHashSet() ?? [];
        var applied = 0;
        foreach (var s in (await ComputeAsync(db, ct)).Where(s => wanted.Contains(s.DeviceId)))
        {
            await db.Devices.Where(d => d.Id == s.DeviceId).ExecuteUpdateAsync(u => u.SetProperty(d => d.ParentDeviceId, s.SuggestedParentId), ct);
            await audit.WriteAsync(http.Actor(), "DependencyApplied", "Device", s.DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{s.DeviceName}: parent {s.CurrentParentName ?? "none"} -> {s.SuggestedParentName} (from {s.Via})");
            applied++;
        }

        notifier.NotifyInventoryChanged();
        return Results.Ok(new { applied });
    }

    private static async Task<IReadOnlyList<DependencySuggestion>> ComputeAsync(NetWatchDbContext db, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Include(d => d.Tags)
            .Select(d => new TopologySuggester.Node(d.Id, d.Name, d.Type, d.ParentDeviceId, d.Tags.Any(t => t.Tag == "core")))
            .ToListAsync(ct);
        var links = await db.NeighborLinks.AsNoTracking().Where(l => l.RemoteDeviceId != null)
            .Select(l => new TopologySuggester.Edge(l.DeviceId, l.RemoteDeviceId!.Value, l.Protocol))
            .ToListAsync(ct);
        return TopologySuggester.Suggest(devices, links);
    }
}

/// <summary>
/// Derives parent dependencies from the physical links (pure, unit tested): in each connected part of the link
/// graph the root is a device tagged "core", otherwise an existing top-level parent with the most links, otherwise
/// the device with the most links. A breadth-first walk from the root gives each device the neighbour it is
/// reached through; where that differs from its configured parent a suggestion is made. Servers and VMs only
/// get a parent; they are never suggested as anyone's parent.
/// </summary>
public static class TopologySuggester
{
    public sealed record Node(int Id, string Name, DeviceType Type, int? ParentId, bool IsCore);

    public sealed record Edge(int From, int To, string Protocol);

    public static List<DependencySuggestion> Suggest(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var adjacency = new Dictionary<int, HashSet<int>>();
        foreach (var e in edges.Where(e => e.From != e.To && byId.ContainsKey(e.From) && byId.ContainsKey(e.To)))
        {
            Add(e.From, e.To);
            Add(e.To, e.From);
        }

        var suggestions = new List<DependencySuggestion>();
        var visited = new HashSet<int>();
        foreach (var start in adjacency.Keys.OrderBy(k => k))
        {
            if (visited.Contains(start))
            {
                continue;
            }

            // Collect the component, then choose its root.
            var component = new List<int>();
            var stack = new Stack<int>([start]);
            visited.Add(start);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                component.Add(n);
                foreach (var m in adjacency[n].Where(visited.Add))
                {
                    stack.Push(m);
                }
            }

            var root = component.Where(n => byId[n].IsCore).OrderByDescending(n => adjacency[n].Count).Cast<int?>().FirstOrDefault()
                ?? component.Where(n => byId[n].ParentId is null && CanBeParent(byId[n])).OrderByDescending(n => adjacency[n].Count).ThenBy(n => n).Cast<int?>().FirstOrDefault()
                ?? component.OrderByDescending(n => adjacency[n].Count).ThenBy(n => n).First();

            var via = new Dictionary<int, int>();
            var queue = new Queue<int>([root]);
            var seen = new HashSet<int> { root };
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();

                // Never route through a server; a server can only be the parent of a VM linked to it directly.
                var serverOnly = n != root && !CanBeParent(byId[n]);
                foreach (var m in adjacency[n].Order().Where(m => (!serverOnly || byId[m].Type == DeviceType.VM) && seen.Add(m)))
                {
                    via[m] = n;
                    queue.Enqueue(m);
                }
            }

            foreach (var (device, parent) in via)
            {
                var d = byId[device];
                if (d.ParentId == parent || d.IsCore)
                {
                    continue;
                }

                suggestions.Add(new DependencySuggestion(d.Id, d.Name, d.ParentId, d.ParentId is { } p && byId.TryGetValue(p, out var cp) ? cp.Name : null,
                    parent, byId[parent].Name, "LLDP/CDP"));
            }
        }

        return suggestions.OrderBy(s => s.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();

        void Add(int a, int b)
        {
            if (!adjacency.TryGetValue(a, out var set))
            {
                adjacency[a] = set = new HashSet<int>();
            }

            set.Add(b);
        }
    }

    private static bool CanBeParent(Node n) => n.Type is DeviceType.Switch or DeviceType.Router or DeviceType.Firewall or DeviceType.Hypervisor or DeviceType.Other;
}
