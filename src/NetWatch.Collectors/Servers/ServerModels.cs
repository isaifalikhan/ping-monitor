using NetWatch.Core;
using NetWatch.Core.Credentials;

namespace NetWatch.Collectors.Servers;

/// <summary>Decrypted Windows or SSH credential for one poll.</summary>
public sealed record ManagementCredential(CredentialKind Kind, CredentialSecret Secret);

public sealed record VolumeReading(string Name, string? Label, string? FileSystem, long TotalBytes, long FreeBytes)
{
    public double UsedPct => TotalBytes <= 0 ? 0 : Math.Round(100.0 * (TotalBytes - FreeBytes) / TotalBytes, 1);
}

public sealed record ServiceReading(string Name, string? DisplayName, ServiceState State, string? StartMode);

public sealed record WindowsEventReading(string LogName, long RecordId, DateTime TimestampUtc, int Level, int EventId, string? Provider, string Message);

/// <summary>What the server collector should do in this poll.</summary>
/// <param name="EventBookmarks">Last event-log record read per log name (empty = start from the last 24 hours).</param>
public sealed record ServerPollRequest(
    string Host,
    ManagementCredential? Credential,
    IReadOnlyList<string> WatchedServices,
    IReadOnlyDictionary<string, long> EventBookmarks,
    string? SshHostKeyFingerprint,
    bool ReadEventLogs,
    bool IncludeWarnings,
    int MaxEventsPerPoll,
    int WinRmPort,
    bool WinRmHttps,
    bool DcomFallback,
    int SshPort,
    TimeSpan Timeout);

/// <summary>Everything the server collector produced. Values that could not be read stay null.</summary>
public sealed class ServerPollData
{
    public ServerCollectionMethod Method { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public DateTime? LastBootUtc { get; set; }
    public bool? PendingReboot { get; set; }
    public DateTime? LastPatchUtc { get; set; }
    public string? LastPatchId { get; set; }
    public double? CpuPct { get; set; }
    public double? MemPct { get; set; }
    public long? MemTotalBytes { get; set; }
    public int? CpuCount { get; set; }
    public double? Load1 { get; set; }
    public double? Load5 { get; set; }
    public double? Load15 { get; set; }

    /// <summary>Total network throughput over all adapters (bits/s), when the method provides rates directly.</summary>
    public double? NetInBps { get; set; }
    public double? NetOutBps { get; set; }

    /// <summary>Raw byte counters (Linux /proc/net/dev); the processor derives rates.</summary>
    public ulong? NetRxBytes { get; set; }
    public ulong? NetTxBytes { get; set; }
    public List<VolumeReading> Volumes { get; } = new();
    public List<ServiceReading> Services { get; } = new();
    public List<WindowsEventReading> Events { get; } = new();

    /// <summary>Set when event logs were read (even if empty), with the new bookmark per log.</summary>
    public Dictionary<string, long>? NewBookmarks { get; set; }
    public string? EventLogError { get; set; }

    /// <summary>SSH: the fingerprint accepted on first use, to be stored on the device.</summary>
    public string? NewSshHostKeyFingerprint { get; set; }
}

public interface IServerCollector
{
    Task<ServerPollData> CollectAsync(ServerPollRequest request, CancellationToken ct);
}
