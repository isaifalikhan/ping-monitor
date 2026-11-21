using NetWatch.Alerts;
using NetWatch.Alerts.Engine;
using NetWatch.Collectors.DomainControllers;
using NetWatch.Collectors.Processing;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Simulator;
using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Core.Credentials;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;

namespace NetWatch.Tests;

/// <summary>Domain controller monitoring: Security-log parsing, replication parsing, filters, grouped rules, working hours.</summary>
public class Phase4DcTests
{
    private static string Event(int id, long record, string data, string? userData = null) => $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Security-Auditing" />
            <EventID>{id}</EventID>
            <TimeCreated SystemTime="2026-11-20T09:15:30.1234567Z" />
            <EventRecordID>{record}</EventRecordID>
            <Channel>Security</Channel>
            <Computer>DC01.corp.local</Computer>
          </System>
          {(userData ?? $"<EventData>{data}</EventData>")}
        </Event>
        """;

    private static string D(string name, string value) => $"<Data Name=\"{name}\">{value}</Data>";

    // ------------------------------------------------------------------ Security-log parsing

    [Fact]
    public void Parses_failed_logon_with_status_and_source()
    {
        var xml = Event(4625, 1001,
            D("SubjectUserName", "-") + D("TargetUserName", "j.doe") + D("TargetDomainName", "CORP") + D("Status", "0xc000006d") +
            D("SubStatus", "0xc000006a") + D("LogonType", "3") + D("WorkstationName", "WS-042") + D("IpAddress", "10.1.2.42"));
        var r = DcEventParser.Parse(xml)!;
        Assert.Equal(4625, r.EventId);
        Assert.Equal(1001, r.RecordId);
        Assert.Equal(new DateTime(2026, 11, 20, 9, 15, 30, DateTimeKind.Utc), r.TimestampUtc, TimeSpan.FromSeconds(1));
        Assert.Equal(DateTimeKind.Utc, r.TimestampUtc.Kind);
        Assert.Equal("DC01.corp.local", r.Computer);
        Assert.Null(r.SubjectUser);
        Assert.Equal("j.doe", r.TargetUser);
        Assert.Equal("10.1.2.42", r.SourceIp);
        Assert.Equal("WS-042", r.Workstation);
        Assert.Equal(3, r.LogonType);
        Assert.True(r.Failed);
        var text = DcEventParser.Describe(r);
        Assert.Contains("CORP\\j.doe", text);
        Assert.Contains("10.1.2.42 (WS-042)", text);
        Assert.Contains("wrong password", text);
    }

    [Fact]
    public void Lockout_takes_caller_computer_from_target_domain()
    {
        var r = DcEventParser.Parse(Event(4740, 5, D("TargetUserName", "a.khan") + D("TargetDomainName", "WS-007") + D("SubjectUserName", "DC01$")))!;
        Assert.Equal("a.khan", r.TargetUser);
        Assert.Null(r.TargetDomain);
        Assert.Equal("WS-007", r.Workstation);
        Assert.False(r.Failed);
    }

    [Fact]
    public void Group_change_maps_group_and_member_name()
    {
        var r = DcEventParser.Parse(Event(4728, 9,
            D("MemberName", @"CN=Khan\, Ali,OU=Staff,DC=corp,DC=local") + D("MemberSid", "S-1-5-21-1-2-3-1104") +
            D("TargetUserName", "Domain Admins") + D("TargetDomainName", "CORP") + D("SubjectUserName", "adm.saif") + D("SubjectDomainName", "CORP")))!;
        Assert.Equal("Domain Admins", r.GroupName);
        Assert.Equal("Khan, Ali", r.TargetUser);
        Assert.Contains("Khan, Ali added to Domain Admins", DcEventParser.Describe(r));
        Assert.Contains("by CORP\\adm.saif", DcEventParser.Describe(r));
    }

    [Fact]
    public void Kerberos_events_normalise_mapped_ipv6_and_detect_failure()
    {
        var failed = DcEventParser.Parse(Event(4768, 11, D("TargetUserName", "s.ali") + D("IpAddress", "::ffff:10.0.0.5") + D("Status", "0x6")))!;
        Assert.Equal("10.0.0.5", failed.SourceIp);
        Assert.True(failed.Failed);
        var ok = DcEventParser.Parse(Event(4768, 12, D("TargetUserName", "s.ali") + D("IpAddress", "::ffff:10.0.0.5") + D("Status", "0x0")))!;
        Assert.False(ok.Failed);
        var ntlm = DcEventParser.Parse(Event(4776, 13, D("TargetUserName", "s.ali") + D("Workstation", "WS-1") + D("Status", "0xc000006a")))!;
        Assert.True(ntlm.Failed);
        Assert.Equal("WS-1", ntlm.Workstation);
    }

    [Fact]
    public void Log_cleared_reads_user_data()
    {
        var xml = Event(1102, 20, string.Empty,
            """<UserData><LogFileCleared xmlns="http://manifests.microsoft.com/win/2004/08/windows/eventlog"><SubjectUserName>adm.saif</SubjectUserName><SubjectDomainName>CORP</SubjectDomainName></LogFileCleared></UserData>""");
        var r = DcEventParser.Parse(xml)!;
        Assert.Equal(1102, r.EventId);
        Assert.Equal("adm.saif", r.SubjectUser);
        Assert.Contains("Security log cleared: CORP\\adm.saif", DcEventParser.Describe(r));
    }

    [Fact]
    public void Unknown_or_malformed_events_are_ignored()
    {
        Assert.Null(DcEventParser.Parse(Event(4688, 1, D("NewProcessName", "cmd.exe"))));
        Assert.Null(DcEventParser.Parse("<Event><System><EventID>4625"));
        Assert.Null(DcEventParser.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><Event>&e;</Event>"));
    }

    // ------------------------------------------------------------------ replication parsing

    [Fact]
    public void Parses_replication_neighbor()
    {
        const string xml = """
            <DS_REPL_NEIGHBOR>
              <pszNamingContext>DC=corp,DC=local</pszNamingContext>
              <pszSourceDsaDN>CN=NTDS Settings,CN=DC02,CN=Servers,CN=HQ,CN=Sites,CN=Configuration,DC=corp,DC=local</pszSourceDsaDN>
              <pszSourceDsaAddress>0b4f-guid._msdcs.corp.local</pszSourceDsaAddress>
              <dwLastSyncResult>1722</dwLastSyncResult>
              <cNumConsecutiveSyncFailures>4</cNumConsecutiveSyncFailures>
              <ftimeLastSyncSuccess>2026-11-20T08:00:00Z</ftimeLastSyncSuccess>
              <ftimeLastSyncAttempt>2026-11-20T09:00:00Z</ftimeLastSyncAttempt>
            </DS_REPL_NEIGHBOR>
            """;
        var r = DcReplicationParser.ParseNeighbor(xml)!;
        Assert.Equal("DC02", r.Partner);
        Assert.Equal("DC=corp,DC=local", r.NamingContext);
        Assert.Equal(1722, r.LastResult);
        Assert.Equal(4, r.ConsecutiveFailures);
        Assert.Equal(new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), r.LastSuccessUtc);
        Assert.Contains("RPC server", DcResultProcessor.Win32Text(1722), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Never_replicated_is_null_and_dn_helpers_work()
    {
        var r = DcReplicationParser.ParseNeighbor(
            "<DS_REPL_NEIGHBOR><pszNamingContext>CN=Schema,CN=Configuration,DC=corp,DC=local</pszNamingContext><pszSourceDsaAddress>x._msdcs</pszSourceDsaAddress><ftimeLastSyncSuccess>1601-01-01T00:00:00Z</ftimeLastSyncSuccess></DS_REPL_NEIGHBOR>")!;
        Assert.Null(r.LastSuccessUtc);
        Assert.Equal("x._msdcs", r.Partner);
        Assert.Equal("Schema", DcReplicationParser.NamingContextLabel(r.NamingContext));
        Assert.Equal("corp.local", DcReplicationParser.NamingContextLabel("DC=corp,DC=local"));
        Assert.Equal("DomainDnsZones", DcReplicationParser.NamingContextLabel("DC=DomainDnsZones,DC=corp,DC=local"));
        Assert.Equal("HQ", DcReplicationParser.SiteFromServerDn("CN=DC01,CN=Servers,CN=HQ,CN=Sites,CN=Configuration,DC=corp,DC=local"));
        Assert.Equal("corp.local", DcReplicationParser.DnsFromDn("DC=corp,DC=local"));
        Assert.Null(DcReplicationParser.ParseNeighbor("<DS_REPL_NEIGHBOR/>"));
    }

    // ------------------------------------------------------------------ settings and filters

    [Fact]
    public void Working_hours_use_pakistan_time()
    {
        var s = new DcSettings();
        s.Normalise();
        Assert.True(s.IsWorkingTime(new DateTime(2026, 11, 16, 5, 0, 0, DateTimeKind.Utc)));   // Mon 10:00 PKT
        Assert.False(s.IsWorkingTime(new DateTime(2026, 11, 16, 2, 30, 0, DateTimeKind.Utc))); // Mon 07:30 PKT
        Assert.False(s.IsWorkingTime(new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc))); // Mon 17:00 PKT (end exclusive)
        Assert.False(s.IsWorkingTime(new DateTime(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc)));  // Sat 11:00 PKT
        s.WorkingDays = "Mon,Tue,Wed,Thu,Fri,Sat";
        Assert.True(s.IsWorkingTime(new DateTime(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc)));
        Assert.True(s.IsPrivilegedGroup("domain admins"));
        Assert.False(s.IsPrivilegedGroup("HR-Staff"));
    }

    private static DcSecurityReading Reading(int id, string? target = "u1", string? subject = null, int? logonType = null, bool failed = false) =>
        new(1, DateTime.UtcNow, id, "DC01", subject, null, target, null, null, null, null, null, logonType, null, null, failed);

    [Fact]
    public void Storage_filters_drop_noise()
    {
        var s = new DcSettings();
        s.Normalise();
        Assert.True(DcResultProcessor.Keep(Reading(4624, logonType: 2), s));
        Assert.False(DcResultProcessor.Keep(Reading(4624, logonType: 3), s));          // network logons not stored by default
        Assert.False(DcResultProcessor.Keep(Reading(4624, "WS-01$", logonType: 2), s)); // computer account
        Assert.False(DcResultProcessor.Keep(Reading(4625, "WS-01$", failed: true), s));
        Assert.True(DcResultProcessor.Keep(Reading(4625, "u1", failed: true), s));
        Assert.False(DcResultProcessor.Keep(Reading(4672, null, "SYSTEM"), s));
        Assert.False(DcResultProcessor.Keep(Reading(4672, null, "DC01$"), s));
        Assert.True(DcResultProcessor.Keep(Reading(4672, null, "adm.saif"), s));
        s.StoreSuccessfulLogons = false;
        Assert.False(DcResultProcessor.Keep(Reading(4624, logonType: 2), s));
        Assert.False(DcResultProcessor.Keep(Reading(4768), s));
        Assert.True(DcResultProcessor.Keep(Reading(4740), s));
    }

    [Fact]
    public void Field_filter_matches_case_insensitively()
    {
        var f = new EventFilter { EventIds = [4672], FieldEquals = new() { ["offHours"] = "true" } };
        var fields = new Dictionary<string, string> { ["offHours"] = "TRUE" };
        Assert.True(f.Matches(new EventSignal(1, EventKeys.DcSecurity, "x", DateTime.UtcNow, Severity.Info, null, 4672, null, fields)));
        Assert.False(f.Matches(new EventSignal(1, EventKeys.DcSecurity, "x", DateTime.UtcNow, Severity.Info, null, 4672, null, new Dictionary<string, string> { ["offHours"] = "false" })));
        Assert.False(f.Matches(new EventSignal(1, EventKeys.DcSecurity, "x", DateTime.UtcNow, Severity.Info, null, 4672)));
        Assert.Equal(f.ToJson(), EventFilter.Parse(f.ToJson()).ToJson());
        Assert.NotNull(new EventFilter { FieldEquals = new() { [""] = "x" } }.Validate());
    }

    // ------------------------------------------------------------------ grouped count rules

    [Fact]
    public void Count_rule_grouped_by_account_raises_per_account()
    {
        var device = new DeviceContext(7, "DC01", "10.0.0.1", DeviceType.DomainController, new HashSet<int>(), new HashSet<string>(), false);
        var rule = new AlertRuleDefinition(30, "Failed logons for one account", AlertRuleType.CountInWindow, Severity.Warning, new RuleScope(), EventKeys.DcSecurity,
            null, null, null, TimeSpan.Zero, new EventFilter { FieldEquals = new() { ["failed"] = "true" } }, TimeSpan.FromMinutes(5), 5, GroupByField: "targetUser");
        var evaluator = new AlertEvaluator();
        var t0 = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        EventSignal Failed(string user, int sec) =>
            new(7, EventKeys.DcSecurity, $"Failed logon: {user}", t0.AddSeconds(sec), Severity.Warning, null, 4625, null,
                new Dictionary<string, string> { ["failed"] = "true", ["targetUser"] = user });

        var transitions = new List<AlertTransition>();
        for (var i = 0; i < 4; i++)
        {
            transitions.AddRange(evaluator.Evaluate(Failed("A.Khan", i * 10), device, [rule]));
            transitions.AddRange(evaluator.Evaluate(Failed("s.ali", (i * 10) + 5), device, [rule]));
        }

        Assert.Empty(transitions); // 4 each: below the threshold, and accounts are not summed

        transitions.AddRange(evaluator.Evaluate(Failed("a.khan", 50), device, [rule]));
        var raise = Assert.Single(transitions);
        Assert.Equal(TransitionKind.Raise, raise.Kind);
        Assert.Equal("Failed logons for one account: DC01 a.khan", raise.Title);
        Assert.Contains("a.khan", raise.DedupKey);

        // Successful or field-less events do not count.
        Assert.Empty(evaluator.Evaluate(new EventSignal(7, EventKeys.DcSecurity, "x", t0.AddSeconds(60), Severity.Info, null, 4625), device, [rule]));

        // The window going quiet resolves the alert.
        var resolved = evaluator.Tick(t0.AddMinutes(20), _ => device, [rule], _ => null);
        Assert.Contains(resolved, t => t.Kind == TransitionKind.Resolve && t.Title.EndsWith("a.khan", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_rules_cover_phase4_requirements()
    {
        var rules = DefaultRules.Create(DateTime.UtcNow);
        AlertRule R(string name) => Assert.Single(rules, r => r.Name == name);

        var perAccount = R("Failed logons for one account");
        Assert.Equal((5, 300, "targetUser"), (perAccount.CountThreshold, perAccount.WindowSec, perAccount.GroupByField));
        var perSource = R("Failed logons from one source");
        Assert.Equal((20, 300, "source"), (perSource.CountThreshold, perSource.WindowSec, perSource.GroupByField));
        Assert.Equal(Severity.Critical, R("Privileged group membership added").Severity);
        Assert.Equal(Severity.Critical, R("Security log cleared").Severity);
        Assert.Equal(Severity.Critical, R("Audit policy changed").Severity);
        Assert.Contains(4740, EventFilter.Parse(R("Account locked out").FilterJson).EventIds);
        Assert.Equal("true", EventFilter.Parse(R("Admin logon outside working hours").FilterJson).FieldEquals["offHours"]);
        Assert.Equal(900, R("AD replication failing").DurationSec);
        Assert.Equal(StateKeys.DcReplicationFailed, R("AD replication failing").Key);
        Assert.Equal(MetricKeys.DcTimeSkew, R("DC clock skew").Key);
        Assert.Null(EventFilter.Parse(R("Directory/DFS replication error").FilterJson).Validate());
        Assert.Equal(rules.Count, rules.Select(r => r.Name).Distinct().Count());
    }

    // ------------------------------------------------------------------ simulator

    [Fact]
    public void Simulated_dc_produces_lockouts_replication_and_skew()
    {
        var start = new DateTime(2026, 11, 20, 4, 0, 0, DateTimeKind.Utc);
        var dc = new SimulatedDomainController(42, "SIM-DC-02", isPdc: false, partner: "SIM-DC-01", start)
        {
            BruteForceProbability = 1,
            ReplicationFailureProbability = 1,
        };
        var credential = new ManagementCredential(CredentialKind.Windows, new CredentialSecret { Username = "u", Password = "p" });
        var bookmarks = new Dictionary<string, long> { [DcLogs.Security] = 0 }; // not first contact: every poll returns its events
        var all = new List<DcSecurityReading>();
        DcPollData? last = null;
        for (var i = 0; i < 10; i++)
        {
            var request = new DcPollRequest("10.0.0.1", credential, bookmarks, true, 5000, true, 5985, false, true, TimeSpan.FromSeconds(10));
            last = dc.Poll(request, start.AddMinutes(i), 1, 60);
            all.AddRange(last.SecurityEvents);
            foreach (var (log, id) in last.NewBookmarks)
            {
                bookmarks[log] = id;
            }
        }

        var lockouts = all.Where(e => e.EventId == 4740).ToList();
        Assert.NotEmpty(lockouts);
        Assert.All(lockouts, lockout =>
            Assert.True(all.Count(e => e.EventId == 4625 && e.TargetUser == lockout.TargetUser && e.TimestampUtc <= lockout.TimestampUtc) >= 5,
                "a lockout must come after the failures that caused it"));
        Assert.True(all.Count(e => e.EventId == 4625 && e.Failed) >= 5);
        Assert.Equal(all.Count, all.Select(e => e.RecordId).Distinct().Count());
        // A restarted simulator (new instance, later start) continues with higher record numbers, like a real log.
        var restarted = new SimulatedDomainController(42, "SIM-DC-02", isPdc: false, partner: "SIM-DC-01", start.AddMinutes(11));
        var next = restarted.Poll(new DcPollRequest("10.0.0.1", credential, bookmarks, true, 5000, true, 5985, false, true, TimeSpan.FromSeconds(10)), start.AddMinutes(11), 1, 60);
        Assert.All(next.SecurityEvents, e => Assert.True(e.RecordId > all.Max(x => x.RecordId)));

        Assert.NotNull(last!.Replication);
        Assert.Contains(last.Replication, l => l.LastResult == 1722 && l.ConsecutiveFailures > 0);
        Assert.Equal("sim-dc-01.sim.local", last.Topology!.PdcEmulator);
        Assert.NotNull(last.SkewToPdcSec);
    }
}
