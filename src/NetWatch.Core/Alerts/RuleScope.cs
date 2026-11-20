using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetWatch.Core.Alerts;

/// <summary>
/// Which devices / interfaces a rule applies to. Each non-empty list is a filter; all filters must match.
/// An empty scope matches every device.
/// </summary>
public sealed class RuleScope
{
    public List<int> DeviceIds { get; set; } = new();

    /// <summary>Matches devices in these groups or any of their sub-groups.</summary>
    public List<int> GroupIds { get; set; } = new();
    public List<DeviceType> DeviceTypes { get; set; } = new();

    /// <summary>Device must carry at least one of these tags.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>For interface signals: only interfaces flagged as important.</summary>
    public bool ImportantInterfacesOnly { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RuleScope Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RuleScope();
        }

        try
        {
            return JsonSerializer.Deserialize<RuleScope>(json, JsonOptions) ?? new RuleScope();
        }
        catch (JsonException)
        {
            // A corrupt scope must never make a rule match everything silently; match nothing instead.
            return new RuleScope { DeviceIds = new List<int> { -1 } };
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public bool Matches(DeviceContext device, bool interfaceIsImportant, bool isInterfaceSignal)
    {
        if (DeviceIds.Count > 0 && !DeviceIds.Contains(device.Id))
        {
            return false;
        }

        if (DeviceTypes.Count > 0 && !DeviceTypes.Contains(device.Type))
        {
            return false;
        }

        if (GroupIds.Count > 0 && !GroupIds.Any(device.GroupAncestry.Contains))
        {
            return false;
        }

        if (Tags.Count > 0 && !Tags.Any(t => device.Tags.Contains(t)))
        {
            return false;
        }

        if (ImportantInterfacesOnly && isInterfaceSignal && !interfaceIsImportant)
        {
            return false;
        }

        return true;
    }
}

/// <summary>Device attributes the alert engine needs for scope matching and suppression.</summary>
/// <param name="GroupAncestry">The device's group id and all ancestor group ids.</param>
public sealed record DeviceContext(
    int Id,
    string Name,
    string IpAddress,
    DeviceType Type,
    IReadOnlySet<int> GroupAncestry,
    IReadOnlySet<string> Tags,
    bool InMaintenance);

public interface IDeviceContextProvider
{
    DeviceContext? Get(int deviceId);
}
