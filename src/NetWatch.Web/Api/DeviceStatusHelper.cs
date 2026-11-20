using NetWatch.Core;
using NetWatch.Core.Entities;

namespace NetWatch.Web.Api;

/// <summary>Combines collector status, maintenance and open alerts into the status shown in the UI.</summary>
public static class DeviceStatusHelper
{
    public static DeviceStatus Effective(DeviceStatus raw, bool enabled, bool inMaintenance, Severity? worstAlert)
    {
        if (inMaintenance)
        {
            return DeviceStatus.Maintenance;
        }

        if (!enabled || raw is DeviceStatus.Down or DeviceStatus.Unknown)
        {
            return raw;
        }

        return raw == DeviceStatus.Warning || worstAlert is >= Severity.Warning ? DeviceStatus.Warning : DeviceStatus.Up;
    }

    public static bool InMaintenance(Device d, DateTime nowUtc) =>
        d.MaintenanceMode && (d.MaintenanceUntilUtc is null || d.MaintenanceUntilUtc > nowUtc);

    /// <summary>Builds "Site / Building / Floor" paths for all groups.</summary>
    public static Dictionary<int, string> GroupPaths(IEnumerable<DeviceGroup> groups)
    {
        var byId = groups.ToDictionary(g => g.Id);
        var result = new Dictionary<int, string>();
        foreach (var g in byId.Values)
        {
            var parts = new List<string>();
            var current = g;
            var guard = 0;
            while (current is not null && guard++ < 64)
            {
                parts.Add(current.Name);
                current = current.ParentId is { } p ? byId.GetValueOrDefault(p) : null;
            }

            parts.Reverse();
            result[g.Id] = string.Join(" / ", parts);
        }

        return result;
    }
}
