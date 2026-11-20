using System.Collections.Concurrent;
using NetWatch.Core;

namespace NetWatch.Collectors.Live;

public sealed record DeviceLive(
    int DeviceId,
    DeviceStatus Status,
    DateTime LastPollUtc,
    double? RttMs,
    double? LossPct,
    double? CpuPct,
    double? MemPct,
    double? TempC,
    long? UptimeSec,
    double PollDurationMs);

public sealed record InterfaceLive(
    int InterfaceId,
    int DeviceId,
    int IfIndex,
    string Name,
    int OperStatus,
    int AdminStatus,
    long SpeedBps,
    double? InBps,
    double? OutBps,
    double? InUtilPct,
    double? OutUtilPct,
    double? ErrorsPs,
    DateTime UpdatedUtc)
{
    public double MaxUtil => Math.Max(InUtilPct ?? 0, OutUtilPct ?? 0);
}

/// <summary>
/// Latest values per device and interface, kept in memory so the overview, device list and port grid
/// do not hit the metrics database.
/// </summary>
public sealed class LiveStateCache
{
    private readonly ConcurrentDictionary<int, DeviceLive> _devices = new();
    private readonly ConcurrentDictionary<int, InterfaceLive> _interfaces = new();

    public void Set(DeviceLive device) => _devices[device.DeviceId] = device;

    public void Set(InterfaceLive iface) => _interfaces[iface.InterfaceId] = iface;

    public DeviceLive? GetDevice(int deviceId) => _devices.GetValueOrDefault(deviceId);

    public InterfaceLive? GetInterface(int interfaceId) => _interfaces.GetValueOrDefault(interfaceId);

    public IEnumerable<DeviceLive> Devices => _devices.Values;

    public IEnumerable<InterfaceLive> InterfacesOf(int deviceId) => _interfaces.Values.Where(i => i.DeviceId == deviceId);

    /// <summary>Busiest interfaces by utilisation (or by throughput when speed is unknown).</summary>
    public IReadOnlyList<InterfaceLive> TopInterfaces(int count) => _interfaces.Values
        .Where(i => i.OperStatus == 1)
        .OrderByDescending(i => i.MaxUtil)
        .ThenByDescending(i => Math.Max(i.InBps ?? 0, i.OutBps ?? 0))
        .Take(count)
        .ToList();

    public void RemoveDevice(int deviceId)
    {
        _devices.TryRemove(deviceId, out _);
        foreach (var key in _interfaces.Where(kv => kv.Value.DeviceId == deviceId).Select(kv => kv.Key).ToList())
        {
            _interfaces.TryRemove(key, out _);
        }
    }

    public int InterfaceCount => _interfaces.Count;
}
