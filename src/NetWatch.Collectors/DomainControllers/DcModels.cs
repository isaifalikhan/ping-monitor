using NetWatch.Collectors.Servers;

namespace NetWatch.Collectors.DomainControllers;

/// <summary>One parsed Security-log event of a domain controller.</summary>
/// <param name="Failed">Logon/authentication failure (4625, 4771, failed 4768/4776).</param>
public sealed record DcSecurityReading(
    long RecordId,
    DateTime TimestampUtc,
    int EventId,
    string? Computer,
    string? SubjectUser,
    string? SubjectDomain,
    string? TargetUser,
    string? TargetDomain,
    string? GroupName,
    string? ObjectName,
    string? SourceIp,
    string? Workstation,
    int? LogonType,
    string? Status,
    string? SubStatus,
    bool Failed,
    string? Detail = null);

/// <summary>Inbound replication state with one partner for one naming context.</summary>
public sealed record DcReplicationReading(
    string Partner,
    string NamingContext,
    DateTime? LastSuccessUtc,
    DateTime? LastAttemptUtc,
    int LastResult,
    int ConsecutiveFailures);

/// <summary>Domain, site and FSMO roles of a DC, read from its rootDSE.</summary>
public sealed record DcTopology(
    string? DcName,
    string? Domain,
    string? Forest,
    string? Site,
    bool IsGlobalCatalog,
    IReadOnlyList<string> Roles,
    string? PdcEmulator);

/// <summary>What the DC collector should do in this poll.</summary>
/// <param name="Bookmarks">Last record read per log ("Security", "Directory Service", "DFS Replication"). Missing = start at the newest record.</param>
/// <param name="ReadSecurityLog">Remote Security-log read (off when events arrive via Windows Event Forwarding).</param>
/// <param name="CheckReplication">Query topology, replication partners and the clock this poll (every few minutes).</param>
public sealed record DcPollRequest(
    string Host,
    ManagementCredential Credential,
    IReadOnlyDictionary<string, long> Bookmarks,
    bool ReadSecurityLog,
    int MaxSecurityEvents,
    bool CheckReplication,
    int WinRmPort,
    bool WinRmHttps,
    bool DcomFallback,
    TimeSpan Timeout,
    bool CheckPorts = false);

/// <summary>Everything the DC collector produced. Parts that failed stay null and carry an error text.</summary>
public sealed class DcPollData
{
    public List<DcSecurityReading> SecurityEvents { get; } = new();

    /// <summary>Directory Service and DFS Replication errors/warnings.</summary>
    public List<WindowsEventReading> DirectoryEvents { get; } = new();

    /// <summary>New bookmark per log that was read.</summary>
    public Dictionary<string, long> NewBookmarks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SecurityLogError { get; set; }

    /// <summary>Set when replication was checked this poll.</summary>
    public bool ReplicationChecked { get; set; }
    public DcTopology? Topology { get; set; }
    public List<DcReplicationReading>? Replication { get; set; }
    public string? ReplicationError { get; set; }

    /// <summary>DC clock minus the NetWatch server clock (seconds).</summary>
    public double? ClockOffsetSec { get; set; }

    /// <summary>DC clock minus the PDC emulator clock (seconds); 0 on the PDC itself.</summary>
    public double? SkewToPdcSec { get; set; }
    public string? TimeError { get; set; }

    /// <summary>TCP reachability of the DC service ports (null when not checked this poll).</summary>
    public List<DcPortCheck>? Ports { get; set; }
}

/// <summary>Result of one TCP connect to a DC service port; Error is null when the port answered.</summary>
public sealed record DcPortCheck(string Service, int Port, string? Error, double? Ms = null);

/// <summary>The DC service ports NetWatch checks.</summary>
public static class DcPorts
{
    public static readonly IReadOnlyList<(string Service, int Port)> All = [("LDAP", 389), ("Kerberos", 88), ("DNS", 53)];

    /// <summary>TCP connect to each port in parallel with a short timeout.</summary>
    public static async Task<List<DcPortCheck>> CheckAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        var tasks = All.Select(async p =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await client.ConnectAsync(host, p.Port, cts.Token);
                return new DcPortCheck(p.Service, p.Port, null, Math.Round(watch.Elapsed.TotalMilliseconds, 1));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new DcPortCheck(p.Service, p.Port, $"no answer within {timeout.TotalSeconds:0} s");
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                return new DcPortCheck(p.Service, p.Port, ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused ? "connection refused" : ex.Message);
            }
        });
        return [.. await Task.WhenAll(tasks)];
    }
}

public interface IDcCollector
{
    Task<DcPollData> CollectAsync(DcPollRequest request, CancellationToken ct);
}

/// <summary>Log names read from domain controllers.</summary>
public static class DcLogs
{
    public const string Security = "Security";
    public const string DirectoryService = "Directory Service";
    public const string DfsReplication = "DFS Replication";
    public const string Forwarded = "ForwardedEvents";

    public static readonly IReadOnlyList<string> All = [Security, DirectoryService, DfsReplication];
}
