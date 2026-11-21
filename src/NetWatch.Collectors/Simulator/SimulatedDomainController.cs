using NetWatch.Collectors.DomainControllers;
using NetWatch.Collectors.Servers;

namespace NetWatch.Collectors.Simulator;

/// <summary>
/// Simulated Active Directory domain controller (domain sim.local): a steady stream of logons, Kerberos and NTLM
/// requests following office hours, occasional mistyped passwords, brute-force bursts that end in a lockout,
/// password sprays, rare account and privileged-group changes, admin logons at odd hours, inbound replication
/// failures (RPC server unavailable) and clock drift on the non-PDC.
/// </summary>
internal sealed class SimulatedDomainController
{
    private static readonly string[] Users =
    [
        "a.khan", "s.ali", "m.raza", "f.ahmed", "h.iqbal", "z.hussain", "n.malik", "u.sheikh", "r.butt", "k.qureshi",
        "t.siddiqui", "i.chaudhry", "y.mirza", "b.akhtar", "o.farooq", "w.javed", "s.naqvi", "a.rehman", "m.tariq", "h.saeed",
        "j.anwar", "l.yousaf", "q.abbasi", "e.nawaz", "d.zafar", "c.aslam", "p.rashid", "g.haider", "v.kamal", "x.shah",
    ];

    private static readonly string[] Admins = ["adm.saif", "adm.ops", "adm.backup"];
    private static readonly string[] NormalGroups = ["HR-Staff", "Finance-Readers", "VPN-Users", "Print-Operators-Floor2"];
    private static readonly string[] PrivilegedGroups = ["Domain Admins", "Administrators", "Backup Operators"];
    private static readonly string[] NamingContexts =
        ["DC=sim,DC=local", "CN=Configuration,DC=sim,DC=local", "CN=Schema,CN=Configuration,DC=sim,DC=local", "DC=DomainDnsZones,DC=sim,DC=local", "DC=ForestDnsZones,DC=sim,DC=local"];

    private readonly Random _rng;
    private readonly string _name;
    private readonly string _partner;
    private readonly bool _isPdc;
    private long _recordId;
    private static readonly DateTime RecordEpoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private long _dsRecordId;

    // Incidents
    private int _bruteForcePolls;
    private string? _bruteForceUser;
    private string? _bruteForceIp;
    private int _bruteForceCount;
    private readonly Dictionary<string, int> _lockedPolls = new(StringComparer.OrdinalIgnoreCase);
    private int _sprayPolls;
    private string? _sprayIp;
    private int _replicationFailPolls;
    private int _portOutagePolls;
    private DateTime _lastReplicationSuccess;
    private int _replicationFailures;
    private double _clockOffset;
    private int _driftPolls;

    public SimulatedDomainController(int deviceId, string name, bool isPdc, string partner, DateTime startUtc)
    {
        _rng = new Random(deviceId * 104_729);
        _name = name;
        _isPdc = isPdc;
        _partner = partner;
        // Record numbers grow with time (at most 50 per second), so they keep increasing across restarts like a real log.
        var seconds = (long)Math.Max(0, (startUtc - RecordEpoch).TotalSeconds);
        _recordId = seconds * 50;
        _dsRecordId = seconds / 10;
        _lastReplicationSuccess = startUtc;
        _clockOffset = isPdc ? 0 : 0.4;
    }

    /// <summary>
    /// Multiplies all incident probabilities, for demonstrations and end-to-end tests of the DC alert rules
    /// (environment variable NETWATCH_SIM_INCIDENT_RATE, 1–1000; default 1).
    /// </summary>
    internal static readonly double IncidentRate = double.TryParse(Environment.GetEnvironmentVariable("NETWATCH_SIM_INCIDENT_RATE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate)
        ? Math.Clamp(rate, 1, 1000)
        : 1;

    internal double BruteForceProbability { get; set; } = 0.004 * IncidentRate;
    internal double SprayProbability { get; set; } = 0.0015 * IncidentRate;
    internal double ReplicationFailureProbability { get; set; } = 0.002 * IncidentRate;

    public DcPollData Poll(DcPollRequest request, DateTime now, double diurnal, double elapsedSec)
    {
        var data = new DcPollData();
        var events = new List<DcSecurityReading>();
        Generate(events, now, diurnal, elapsedSec);
        UpdateIncidents(now);

        if (request.ReadSecurityLog)
        {
            if (request.Bookmarks.ContainsKey(DcLogs.Security))
            {
                data.SecurityEvents.AddRange(events.Take(request.MaxSecurityEvents));
            }

            data.NewBookmarks[DcLogs.Security] = _recordId;
        }

        if (!request.Bookmarks.ContainsKey(DcLogs.DirectoryService))
        {
            data.NewBookmarks[DcLogs.DirectoryService] = _dsRecordId;
            data.NewBookmarks[DcLogs.DfsReplication] = _dsRecordId;
        }
        else if (_replicationFailures == 1 || (_replicationFailPolls > 0 && _rng.NextDouble() < 0.1))
        {
            // The Directory Service log reports the failing partner (2087: DNS lookup failure, 1864: not replicated for a while).
            _dsRecordId++;
            var (id, text) = _replicationFailures == 1
                ? (2087, $"Active Directory Domain Services could not resolve the DNS host name of the source domain controller {_partner}.sim.local. Replication of the directory partition DC=sim,DC=local failed with error 1722 (The RPC server is unavailable).")
                : (1864, $"This is the replication status for the following directory partition on this directory server. More than 24 hours since last successful replication with {_partner}.");
            data.DirectoryEvents.Add(new WindowsEventReading(DcLogs.DirectoryService, _dsRecordId, now, 2, id, "Microsoft-Windows-ActiveDirectory_DomainService", text));
            data.NewBookmarks[DcLogs.DirectoryService] = _dsRecordId;
        }

        if (request.CheckPorts)
        {
            if (_portOutagePolls > 0)
            {
                _portOutagePolls--;
            }
            else if (!_isPdc && _rng.NextDouble() < 0.002)
            {
                _portOutagePolls = 5;
            }

            data.Ports = DcPorts.All.Select(p => p.Service == "DNS" && _portOutagePolls > 0
                ? new DcPortCheck(p.Service, p.Port, "connection refused")
                : new DcPortCheck(p.Service, p.Port, null, Math.Round(0.4 + _rng.NextDouble(), 1))).ToList();
        }

        if (request.CheckReplication)
        {
            data.ReplicationChecked = true;
            data.Topology = new DcTopology(
                $"{_name.ToLowerInvariant()}.sim.local", "sim.local", "sim.local", "HQ", IsGlobalCatalog: true,
                _isPdc ? ["PDC", "RID", "Infrastructure", "Schema", "Domain naming"] : [],
                _isPdc ? $"{_name.ToLowerInvariant()}.sim.local" : $"{_partner.ToLowerInvariant()}.sim.local");
            data.Replication = NamingContexts.Select(nc =>
            {
                var failing = _replicationFailPolls > 0 && nc is "DC=sim,DC=local" or "DC=DomainDnsZones,DC=sim,DC=local";
                return new DcReplicationReading(_partner, nc, failing ? _lastReplicationSuccess : now.AddSeconds(-_rng.Next(30, 900)), now.AddSeconds(-_rng.Next(5, 60)),
                    failing ? 1722 : 0, failing ? _replicationFailures : 0);
            }).ToList();
            data.ClockOffsetSec = Math.Round(_clockOffset);
            data.SkewToPdcSec = _isPdc ? 0 : Math.Round(_clockOffset, 1);
        }

        return data;
    }

    private void UpdateIncidents(DateTime now)
    {
        if (_replicationFailPolls > 0)
        {
            _replicationFailPolls--;
            _replicationFailures++;
            if (_replicationFailPolls == 0)
            {
                _replicationFailures = 0;
                _lastReplicationSuccess = now;
            }
        }
        else if (_rng.NextDouble() < ReplicationFailureProbability)
        {
            // 10–40 minutes: some recover before the 15-minute alert delay, some do not.
            _replicationFailPolls = _rng.Next(10, 41);
            _replicationFailures = 1;
        }
        else
        {
            _lastReplicationSuccess = now;
        }

        if (_isPdc)
        {
            return;
        }

        if (_driftPolls > 0)
        {
            _driftPolls--;
            _clockOffset += 2.5 + _rng.NextDouble();
            if (_driftPolls == 0)
            {
                _clockOffset = 0.4;
            }
        }
        else if (_rng.NextDouble() < 0.0008 * IncidentRate)
        {
            _driftPolls = _rng.Next(15, 40);
        }
        else
        {
            _clockOffset = 0.4 + ((_rng.NextDouble() - 0.5) * 0.4);
        }
    }

    private void Generate(List<DcSecurityReading> events, DateTime now, double diurnal, double elapsedSec)
    {
        DateTime At() => now.AddSeconds(-_rng.NextDouble() * Math.Max(1, elapsedSec));
        string Ws() => $"WS-{_rng.Next(1, 121):D3}";
        string Ip(string ws) => $"10.250.50.{int.Parse(ws[3..], System.Globalization.CultureInfo.InvariantCulture)}";
        string User() => Users[_rng.Next(Users.Length)];

        void Add(int eventId, string? target, string? subject = null, string? ip = null, string? ws = null, int? logonType = null,
            string? status = null, string? subStatus = null, bool failed = false, string? group = null, string? obj = null, string? detail = null, DateTime? at = null)
        {
            events.Add(new DcSecurityReading(++_recordId, at ?? At(), eventId, $"{_name}.sim.local", subject ?? $"{_name}$", "SIM", target,
                target is null ? null : "SIM", group, obj, ip, ws, logonType, status, subStatus, failed, detail));
        }

        // ---- normal office traffic (heavier during the working day)
        // 7/3 events per logon below (4768 + 4624, plus one 4776 per three logons); a load test sets the total generated rate
        // over both DCs. Fewer are stored: by default only interactive/RDP/unlock/cached 4624 logons are kept.
        var logons = SimLoad.DcEventsPerMinute > 0
            ? (int)Math.Round(SimLoad.DcEventsPerMinute / 2 / (7.0 / 3) * (Math.Max(1, elapsedSec) / 60) * (0.9 + (_rng.NextDouble() * 0.2)))
            : (int)Math.Round((4 + (diurnal * 30)) * (0.7 + (_rng.NextDouble() * 0.6)));
        for (var i = 0; i < logons; i++)
        {
            var ws = Ws();
            var user = User();
            var type = _rng.NextDouble() switch { < 0.55 => 3, < 0.85 => 2, < 0.95 => 7, _ => 10 };
            Add(4768, user, ip: Ip(ws), status: "0x0");
            Add(4624, user, ip: Ip(ws), ws: ws, logonType: type);
        }

        for (var i = 0; i < logons / 3; i++)
        {
            var ws = Ws();
            Add(4776, User(), ws: ws, status: "0x0");
        }

        // A few mistyped passwords.
        if (_rng.NextDouble() < 0.35)
        {
            var ws = Ws();
            Add(4771, User(), ip: Ip(ws), status: "0x18", failed: true);
        }

        if (_rng.NextDouble() < 0.15)
        {
            var ws = Ws();
            Add(4625, User(), ip: Ip(ws), ws: ws, logonType: 3, status: "0xc000006d", subStatus: "0xc000006a", failed: true);
        }

        // ---- brute force against one account → lockout
        if (_bruteForcePolls == 0 && _rng.NextDouble() < BruteForceProbability)
        {
            _bruteForcePolls = _rng.Next(2, 5);
            _bruteForceUser = User();
            _bruteForceIp = $"10.250.66.{_rng.Next(10, 250)}";
            _bruteForceCount = 0;
        }

        if (_bruteForcePolls > 0 && _bruteForceUser is { } victim)
        {
            _bruteForcePolls--;
            var attempts = _rng.Next(3, 7);
            for (var i = 0; i < attempts; i++)
            {
                // Attempts are spread evenly over the poll interval so the lockout comes after the failures that caused it.
                var at = now.AddSeconds(-elapsedSec + ((i + 1) * elapsedSec / (attempts + 1)));
                Add(4625, victim, ip: _bruteForceIp, ws: "UNKNOWN", logonType: 3, status: "0xc000006d", subStatus: "0xc000006a", failed: true, at: at);
                if (++_bruteForceCount == 6 && !_lockedPolls.ContainsKey(victim))
                {
                    // Lockout threshold reached on the PDC; TargetDomainName (here: workstation) is the caller computer.
                    Add(4740, victim, ws: "UNKNOWN", at: at.AddMilliseconds(200));
                    _lockedPolls[victim] = _rng.Next(20, 60);
                }
            }
        }

        foreach (var (user, polls) in _lockedPolls.ToList())
        {
            if (polls <= 1)
            {
                Add(4767, user, subject: Admins[0]);
                _lockedPolls.Remove(user);
            }
            else
            {
                _lockedPolls[user] = polls - 1;
            }
        }

        // ---- password spray: one source, many accounts
        if (_sprayPolls == 0 && _rng.NextDouble() < SprayProbability)
        {
            _sprayPolls = 2;
            _sprayIp = $"10.250.77.{_rng.Next(10, 250)}";
        }

        if (_sprayPolls > 0)
        {
            _sprayPolls--;
            foreach (var user in Users.OrderBy(_ => _rng.Next()).Take(14))
            {
                Add(4771, user, ip: _sprayIp, status: "0x18", failed: true);
            }
        }

        // ---- administration
        var working = diurnal > 0.3;
        if (_rng.NextDouble() < (working ? 0.08 : 0.004 * IncidentRate))
        {
            var admin = Admins[_rng.Next(Admins.Length)];
            var ws = $"ADM-{_rng.Next(1, 4):D2}";
            Add(4624, admin, ip: Ip("WS-200"), ws: ws, logonType: 10);
            Add(4672, null, subject: admin, detail: "SeBackupPrivilege, SeRestorePrivilege, SeDebugPrivilege, SeTakeOwnershipPrivilege");
        }

        Rare(0.004, () => Add(4738, User(), subject: Admins[0]));
        Rare(0.003, () => Add(4724, User(), subject: Admins[1]));
        Rare(0.004, () => Add(5136, null, subject: Admins[0], obj: $"CN={User()},OU=Staff,DC=sim,DC=local", detail: "set telephoneNumber"));
        Rare(0.0012, () => Add(4720, $"new.user{_rng.Next(100, 999)}", subject: Admins[0]));
        Rare(0.0008, () => Add(4722, User(), subject: Admins[0]));
        Rare(0.0008, () => Add(4725, User(), subject: Admins[1]));
        Rare(0.0004, () => Add(4726, $"old.user{_rng.Next(100, 999)}", subject: Admins[0]));
        Rare(0.0010, () => Add(4741, $"WS-{_rng.Next(121, 300):D3}$", subject: Admins[1]));
        Rare(0.0004, () => Add(4743, $"WS-{_rng.Next(121, 300):D3}$", subject: Admins[1]));
        Rare(0.003, () =>
        {
            var user = User();
            var group = NormalGroups[_rng.Next(NormalGroups.Length)];
            Add(_rng.Next(2) == 0 ? 4728 : 4729, user, subject: Admins[0], group: group);
        });
        Rare(0.0004, () => Add(4728, Users[_rng.Next(Users.Length)], subject: Admins[0], group: PrivilegedGroups[_rng.Next(PrivilegedGroups.Length)]));
        Rare(0.0003, () => Add(4732, Admins[2], subject: Admins[0], group: "Backup Operators"));
        Rare(0.00005, () => Add(4719, null, subject: Admins[0], detail: "changes Success removed"));
        Rare(0.00003, () => Add(1102, null, subject: Admins[0]));

        void Rare(double probability, Action action)
        {
            if (_rng.NextDouble() < probability * IncidentRate)
            {
                action();
            }
        }
    }
}
