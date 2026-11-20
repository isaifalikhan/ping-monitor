using NetWatch.Collectors.Live;
using NetWatch.Core;
using NetWatch.Core.Alerts;

namespace NetWatch.Collectors.Inventory;

/// <summary>
/// Walks the upstream (parent) chain of a device and reports the nearest parent that is unreachable. A parent
/// counts as unreachable as soon as its last poll failed, not only after it is declared Down: children usually
/// reach their own Down threshold at the same time as the parent, and must not raise alerts in that race.
/// </summary>
public sealed class DependencyStatus : IDependencyStatus
{
    private const int MaxDepth = 10;

    private readonly InventoryCache _inventory;
    private readonly LiveStateCache _live;

    public DependencyStatus(InventoryCache inventory, LiveStateCache live)
    {
        _inventory = inventory;
        _live = live;
    }

    public string? FindUnreachableUpstream(int deviceId)
    {
        var visited = new HashSet<int> { deviceId };
        var current = _inventory.GetSnapshot(deviceId)?.ParentDeviceId;
        for (var depth = 0; current is { } parentId && depth < MaxDepth && visited.Add(parentId); depth++)
        {
            var parent = _inventory.GetSnapshot(parentId);
            if (parent is null)
            {
                return null;
            }

            if (parent.IsEnabled &&
                (_live.IsReachable(parentId) == false || _live.GetDevice(parentId)?.Status == DeviceStatus.Down))
            {
                return parent.Name;
            }

            current = parent.ParentDeviceId;
        }

        return null;
    }
}
