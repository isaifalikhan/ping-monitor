using System.Diagnostics.Eventing.Reader;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Servers.Windows;
using NetWatch.Core;

namespace NetWatch.Collectors.DomainControllers;

/// <summary>
/// Agentless domain controller collection with a least-privilege domain account:
/// <list type="bullet">
/// <item>Security log (account in "Event Log Readers") – the event IDs of <see cref="DcEventCatalog"/>, resumed by bookmark;</item>
/// <item>Directory Service and DFS Replication logs – errors and warnings;</item>
/// <item>rootDSE and <c>msDS-NCReplInboundNeighbors</c> over signed and sealed LDAP – site, roles, PDC and inbound replication per partner;</item>
/// <item>Win32_UTCTime over WinRM/DCOM – clock offset of the DC and of the PDC emulator.</item>
/// </list>
/// Every part fails on its own, so a missing permission for one never hides the others.
/// </summary>
public sealed class DcCollector : IDcCollector
{
    /// <summary>
    /// The Windows event log refuses an XPath with more than about 20 EventID conditions ("The specified query is
    /// invalid"), and the catalog has more. So the IDs are queried in groups and the results merged by record ID.
    /// </summary>
    private const int IdsPerQuery = 10;

    private static readonly string[] SecurityXPathIdGroups = DcEventCatalog.Events.Keys.Order().Chunk(IdsPerQuery)
        .Select(group => string.Join(" or ", group.Select(id => string.Create(CultureInfo.InvariantCulture, $"EventID={id}"))))
        .ToArray();

    /// <summary>One XPath per group of catalogued Security event IDs, each with <paramref name="condition"/> added.</summary>
    internal static IReadOnlyList<string> SecurityXPaths(string condition) =>
        SecurityXPathIdGroups.Select(ids => $"*[System[({ids}) and {condition}]]").ToArray();

    private readonly ILogger<DcCollector> _logger;
    private readonly TimeProvider _time;

    public DcCollector(TimeProvider time, ILogger<DcCollector> logger)
    {
        _time = time;
        _logger = logger;
    }

    public async Task<DcPollData> CollectAsync(DcPollRequest request, CancellationToken ct)
    {
        var account = WindowsAccount.From(request.Credential.Secret);
        var data = new DcPollData();
        var budget = request.Timeout + TimeSpan.FromSeconds(10);

        if (request.CheckPorts)
        {
            data.Ports = await DcPorts.CheckAsync(request.Host, TimeSpan.FromSeconds(3), ct);
        }

        await Part(() => ReadLogs(request, account, data), e => data.SecurityLogError = e, "event logs");
        if (request.CheckReplication)
        {
            data.ReplicationChecked = true;
            await Part(() => ReadDirectory(request, account, data), e => data.ReplicationError = e, "replication");
            await Part(() => ReadClock(request, account, data), e => data.TimeError = e, "clock");
        }

        return data;

        async Task Part(Action action, Action<string> fail, string name)
        {
            try
            {
                await Task.Run(action, ct).WaitAsync(budget, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                fail(Describe(ex));
                _logger.LogDebug(ex, "DC {Part} collection failed for {Host}", name, request.Host);
            }
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException => "timed out",
        UnauthorizedAccessException => "access denied (add the account to \"Event Log Readers\")",
        EventLogNotFoundException => "log not found",
        LdapException ldap => $"LDAP: {ldap.Message.Trim()} ({ldap.ErrorCode})",
        DirectoryOperationException dir => $"LDAP: {dir.Message.Trim()}",
        _ => ex.Message.Trim(),
    };

    // ------------------------------------------------------------------ event logs

    private void ReadLogs(DcPollRequest request, WindowsAccount account, DcPollData data)
    {
        using var session = LocalHost.Is(request.Host)
            ? new EventLogSession()
            : new EventLogSession(request.Host, account.Domain, account.User, account.SecurePassword(), SessionAuthentication.Negotiate);

        var errors = new List<string>();
        if (request.ReadSecurityLog)
        {
            TryLog(DcLogs.Security, () => ReadSecurity(session, request, data));
        }

        foreach (var log in new[] { DcLogs.DirectoryService, DcLogs.DfsReplication })
        {
            TryLog(log, () => ReadDirectoryLog(session, log, request, data));
        }

        if (errors.Count > 0)
        {
            data.SecurityLogError = string.Join(" | ", errors);
        }

        void TryLog(string log, Action read)
        {
            try
            {
                read();
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
            {
                // DFS Replication is absent on DCs still using FRS: not an error worth reporting.
                if (!(log == DcLogs.DfsReplication && ex is EventLogNotFoundException))
                {
                    errors.Add($"{log}: {Describe(ex)}");
                }

                _logger.LogDebug(ex, "Reading {Log} on {Host} failed", log, request.Host);
            }
        }
    }

    private static void ReadSecurity(EventLogSession session, DcPollRequest request, DcPollData data)
    {
        if (!request.Bookmarks.TryGetValue(DcLogs.Security, out var last))
        {
            // First contact: start at the newest record, or — with a backfill window — just before the oldest
            // catalogued event of the window, so the next polls read that history in batches of MaxSecurityEvents.
            var newestRecord = NewestRecordId(session, DcLogs.Security) ?? 0;
            long? oldestInWindow = null;
            if (request.BackfillHours > 0)
            {
                foreach (var xpath in BackfillXPaths(request.BackfillHours))
                {
                    using var reader = new EventLogReader(new EventLogQuery(DcLogs.Security, PathType.LogName, xpath) { Session = session });
                    using var first = reader.ReadEvent();
                    if (first?.RecordId is { } id && (oldestInWindow is null || id < oldestInWindow))
                    {
                        oldestInWindow = id;
                    }
                }
            }

            foreach (var (log, recordId) in FirstContactBookmarks(newestRecord, oldestInWindow))
            {
                data.NewBookmarks[log] = recordId;
            }

            return;
        }

        // Each group returns its events oldest first, up to the limit. Merged and cut at the limit, the newest kept
        // record is never past an event a group left unread, so the bookmark skips nothing.
        var read = new List<(long RecordId, string Xml)>();
        foreach (var xpath in SecurityXPaths(string.Create(CultureInfo.InvariantCulture, $"EventRecordID > {last}")))
        {
            using var reader = new EventLogReader(new EventLogQuery(DcLogs.Security, PathType.LogName, xpath) { Session = session });
            for (var i = 0; i < request.MaxSecurityEvents; i++)
            {
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                if (record.RecordId is { } id)
                {
                    read.Add((id, record.ToXml()));
                }
            }
        }

        var newest = last;
        foreach (var (recordId, xml) in read.OrderBy(r => r.RecordId).Take(request.MaxSecurityEvents))
        {
            newest = recordId;
            if (DcEventParser.Parse(xml) is { } parsed)
            {
                data.SecurityEvents.Add(parsed);
            }
        }

        data.NewBookmarks[DcLogs.Security] = newest;
    }

    private static void ReadDirectoryLog(EventLogSession session, string log, DcPollRequest request, DcPollData data)
    {
        if (!request.Bookmarks.TryGetValue(log, out var last))
        {
            data.NewBookmarks[log] = NewestRecordId(session, log) ?? 0;
            return;
        }

        var xpath = string.Create(CultureInfo.InvariantCulture, $"*[System[(Level=1 or Level=2 or Level=3) and EventRecordID > {last}]]");
        var newest = last;
        using (var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { Session = session }))
        {
            for (var i = 0; i < 1000; i++)
            {
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                var id = record.RecordId ?? 0;
                newest = Math.Max(newest, id);
                data.DirectoryEvents.Add(new WindowsEventReading(log, id, (record.TimeCreated ?? DateTime.Now).ToUniversalTime(),
                    record.Level ?? 2, record.Id, record.ProviderName, Message(record)));
            }
        }

        data.NewBookmarks[log] = newest;
    }

    /// <summary>XPath for the catalogued Security events written within the last <paramref name="hours"/> hours (oldest first).</summary>
    internal static IReadOnlyList<string> BackfillXPaths(int hours) =>
        SecurityXPaths(string.Create(CultureInfo.InvariantCulture, $"TimeCreated[timediff(@SystemTime) <= {(long)hours * 3_600_000}]"));

    /// <summary>
    /// Bookmarks written on the first Security-log contact. Without history in the window the read starts after
    /// the newest record; otherwise just before the oldest event of the window, and the newest record ID is kept
    /// as the end of the backfill (those events are stored but raise no alerts).
    /// </summary>
    internal static IReadOnlyList<(string Log, long RecordId)> FirstContactBookmarks(long newestRecordId, long? oldestInWindowRecordId) =>
        oldestInWindowRecordId is { } oldest && oldest <= newestRecordId
            ? [(DcLogs.Security, Math.Max(0, oldest - 1)), (DcLogs.SecurityBackfillEnd, newestRecordId)]
            : [(DcLogs.Security, newestRecordId)];

    private static long? NewestRecordId(EventLogSession session, string log)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, "*") { Session = session, ReverseDirection = true });
        using var record = reader.ReadEvent();
        return record?.RecordId;
    }

    private static string Message(EventRecord record)
    {
        try
        {
            if (record.FormatDescription() is { Length: > 0 } text)
            {
                return text.Length <= 4000 ? text.Trim() : text[..4000] + "…";
            }
        }
        catch (EventLogException)
        {
            // Provider metadata unavailable remotely.
        }

        var props = record.Properties?.Select(p => p.Value?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? [];
        return props.Count > 0 ? string.Join(" | ", props) : $"Event {record.Id} from {record.ProviderName}";
    }

    // ------------------------------------------------------------------ LDAP: topology and replication

    private static void ReadDirectory(DcPollRequest request, WindowsAccount account, DcPollData data)
    {
        using var ldap = new LdapConnection(new LdapDirectoryIdentifier(request.Host, 389, fullyQualifiedDnsHostName: false, connectionless: false))
        {
            Timeout = request.Timeout,
            AuthType = AuthType.Negotiate,
        };
        ldap.SessionOptions.ProtocolVersion = 3;
        ldap.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        ldap.SessionOptions.Signing = true;
        ldap.SessionOptions.Sealing = true;
        ldap.Bind(new NetworkCredential(account.User, account.Password, account.Domain));

        var root = Single(ldap, string.Empty, "dnsHostName", "serverName", "defaultNamingContext", "rootDomainNamingContext",
            "configurationNamingContext", "schemaNamingContext", "namingContexts", "isGlobalCatalogReady", "dsServiceName")
            ?? throw new InvalidOperationException("rootDSE not readable");

        var domainNc = First(root, "defaultNamingContext");
        var configNc = First(root, "configurationNamingContext");
        var schemaNc = First(root, "schemaNamingContext");
        var dsa = First(root, "dsServiceName");

        string? RoleOwner(string? dn) => dn is null ? null : First(Single(ldap, dn, "fSMORoleOwner"), "fSMORoleOwner");

        var pdcDsa = RoleOwner(domainNc);
        var roles = new List<string>();
        void Role(string name, string? ownerDsa)
        {
            if (ownerDsa is not null && dsa is not null && string.Equals(ownerDsa, dsa, StringComparison.OrdinalIgnoreCase))
            {
                roles.Add(name);
            }
        }

        Role("PDC", pdcDsa);
        Try(() => Role("RID", RoleOwner(domainNc is null ? null : $"CN=RID Manager$,CN=System,{domainNc}")));
        Try(() => Role("Infrastructure", RoleOwner(domainNc is null ? null : $"CN=Infrastructure,{domainNc}")));
        Try(() => Role("Schema", RoleOwner(schemaNc)));
        Try(() => Role("Domain naming", RoleOwner(configNc is null ? null : $"CN=Partitions,{configNc}")));

        // The PDC's DNS name lives on its server object (the parent of the NTDS Settings object).
        string? pdcName = null;
        if (pdcDsa is not null && pdcDsa.IndexOf(',') is var comma and > 0)
        {
            Try(() => pdcName = First(Single(ldap, pdcDsa[(comma + 1)..], "dNSHostName"), "dNSHostName"));
            pdcName ??= DcReplicationParser.ServerNameFromDsaDn(pdcDsa);
        }

        data.Topology = new DcTopology(
            First(root, "dnsHostName"),
            DcReplicationParser.DnsFromDn(domainNc),
            DcReplicationParser.DnsFromDn(First(root, "rootDomainNamingContext")),
            DcReplicationParser.SiteFromServerDn(First(root, "serverName")),
            string.Equals(First(root, "isGlobalCatalogReady"), "TRUE", StringComparison.OrdinalIgnoreCase),
            roles,
            pdcName);

        var links = new List<DcReplicationReading>();
        foreach (var nc in All(root, "namingContexts"))
        {
            var entry = Single(ldap, nc, "msDS-NCReplInboundNeighbors");
            if (entry?.Attributes["msDS-NCReplInboundNeighbors"] is not { } attr)
            {
                continue;
            }

            foreach (var value in attr.GetValues(typeof(string)).Cast<string>())
            {
                if (DcReplicationParser.ParseNeighbor(value) is { } link)
                {
                    links.Add(link);
                }
            }
        }

        data.Replication = links;

        static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is DirectoryOperationException or LdapException)
            {
                // A role object the account may not read: leave the role out.
            }
        }
    }

    private static SearchResultEntry? Single(LdapConnection ldap, string baseDn, params string[] attributes)
    {
        var response = (SearchResponse)ldap.SendRequest(new SearchRequest(baseDn, "(objectClass=*)", SearchScope.Base, attributes));
        return response.Entries.Count > 0 ? response.Entries[0] : null;
    }

    private static string? First(SearchResultEntry? entry, string attribute) =>
        entry?.Attributes[attribute] is { Count: > 0 } a ? a.GetValues(typeof(string)).Cast<string>().FirstOrDefault() : null;

    private static IEnumerable<string> All(SearchResultEntry entry, string attribute) =>
        entry.Attributes[attribute] is { Count: > 0 } a ? a.GetValues(typeof(string)).Cast<string>().ToList() : [];

    // ------------------------------------------------------------------ clock

    private void ReadClock(DcPollRequest request, WindowsAccount account, DcPollData data)
    {
        var dcOffset = ClockOffset(request, request.Host, account);
        data.ClockOffsetSec = dcOffset;

        var pdc = data.Topology?.PdcEmulator;
        var isPdc = data.Topology?.Roles.Contains("PDC") == true;
        if (isPdc)
        {
            data.SkewToPdcSec = 0;
        }
        else if (pdc is not null)
        {
            data.SkewToPdcSec = Math.Round(dcOffset - ClockOffset(request, pdc, account), 1);
        }
    }

    /// <summary>Remote UTC clock minus the local clock, measured at the midpoint of the WMI query.</summary>
    private double ClockOffset(DcPollRequest request, string host, WindowsAccount account)
    {
        Exception? last = null;
        var opens = new List<Func<IWmiSession>> { () => new WinRmWmiSession(host, request.WinRmPort, request.WinRmHttps, account, request.Timeout) };
        if (request.DcomFallback)
        {
            opens.Add(() => new DcomWmiSession(host, account, request.Timeout));
        }

        foreach (var open in opens)
        {
            try
            {
                using var session = open();
                var before = _time.GetUtcNow().UtcDateTime;
                var row = session.Query("root/cimv2", "SELECT * FROM Win32_UTCTime").FirstOrDefault() ?? throw new InvalidOperationException("Win32_UTCTime returned no instance.");
                var after = _time.GetUtcNow().UtcDateTime;
                var remote = new DateTime(
                    (int)(WmiValue.Long(row.GetValueOrDefault("Year")) ?? 0), (int)(WmiValue.Long(row.GetValueOrDefault("Month")) ?? 1), (int)(WmiValue.Long(row.GetValueOrDefault("Day")) ?? 1),
                    (int)(WmiValue.Long(row.GetValueOrDefault("Hour")) ?? 0), (int)(WmiValue.Long(row.GetValueOrDefault("Minute")) ?? 0), (int)(WmiValue.Long(row.GetValueOrDefault("Second")) ?? 0),
                    DateTimeKind.Utc);
                var mid = before + (after - before) / 2;

                // Win32_UTCTime has one-second resolution; round so a steady clock does not jitter by ±0.5 s.
                return Math.Round((remote - mid).TotalSeconds);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException($"clock of {host} not readable: {(last is null ? "no method" : Describe(last))}", last);
    }
}
