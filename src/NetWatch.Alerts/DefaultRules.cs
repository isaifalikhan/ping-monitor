using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;

namespace NetWatch.Alerts;

/// <summary>
/// Built-in rules seeded on first start and added on upgrade when missing (prompt §10 default thresholds). They
/// are editable and can be disabled, but not deleted.
/// </summary>
public static class DefaultRules
{
    /// <summary>Built-in rules whose name changed; the seeder renames the existing rule (keeping its settings).</summary>
    public static readonly IReadOnlyList<(string Old, string New)> Renamed =
    [
        ("NetWatch self-check failing", "HIT SIEM self-check failing"),
    ];

    public static IReadOnlyList<AlertRule> Create(DateTime nowUtc)
    {
        var important = new RuleScope { ImportantInterfacesOnly = true }.ToJson();
        var all = new RuleScope().ToJson();

        return
        [
            // ---- Phase 1: availability, switches, health
            State("Device down", "Device is not reachable (ping and SNMP) for 3 consecutive polls.", StateKeys.DeviceDown, Severity.Critical, 0, all),
            Threshold("High ping latency", MetricKeys.PingRtt, ComparisonOperator.GreaterThan, 100, 80, 300, Severity.Warning, all),
            Threshold("Ping packet loss", MetricKeys.PingLoss, ComparisonOperator.GreaterThan, 20, 10, 180, Severity.Warning, all),
            Threshold("High CPU", MetricKeys.Cpu, ComparisonOperator.GreaterThan, 90, 85, 300, Severity.Major, all),
            Threshold("High memory", MetricKeys.Memory, ComparisonOperator.GreaterThan, 90, 85, 300, Severity.Major, all),
            State("Important port down", "An interface flagged as important (uplink/server port) is down while admin-up.", StateKeys.InterfaceDown, Severity.Major, 0, important),
            Threshold("High interface utilization", MetricKeys.InterfaceUtilization, ComparisonOperator.GreaterThan, 90, 80, 300, Severity.Warning, important),
            State("Port flapping", "A port changed between up and down at least 4 times within 10 minutes (Settings: flap changes / window).", StateKeys.InterfaceFlapping, Severity.Warning, 0, all),
            State("Important port half duplex", "An important port is up in half duplex (duplex mismatch: late collisions, slow throughput).", StateKeys.InterfaceHalfDuplex, Severity.Major, 0, important),
            State("Important port below expected speed", "An important port negotiated a lower speed than its expected speed (e.g. a 10G uplink at 1G).", StateKeys.InterfaceSpeedLow, Severity.Warning, 0, important),
            State("Hardware sensor problem", "A fan, power supply or temperature sensor reports a warning or failure.", StateKeys.SensorProblem, Severity.Major, 0, all),
            State("SNMP not responding", "Device answers ping but not SNMP (check credentials/ACL).", StateKeys.SnmpUnreachable, Severity.Warning, 180, all),
            Event("Device rebooted", "sysUpTime went backwards: the device restarted.", EventKeys.DeviceReboot, Severity.Warning, all, null),

            // ---- Phase 2: servers
            Threshold("Disk space low", MetricKeys.DiskUsed, ComparisonOperator.GreaterThan, 85, 80, 120, Severity.Warning, all),
            Threshold("Disk space critical", MetricKeys.DiskUsed, ComparisonOperator.GreaterThan, 95, 92, 120, Severity.Critical, all),
            State("Service not running", "A watched Windows service or systemd unit is not running.", StateKeys.ServiceDown, Severity.Major, 60, all),
            State("Server monitoring failing", "The server answers ping but WinRM/WMI or SSH collection fails (credentials, firewall, permissions).", StateKeys.ServerCollectionFailed, Severity.Warning, 300, all),
            Threshold("High load (Linux)", MetricKeys.LoadPerCpu, ComparisonOperator.GreaterThan, 2, 1.5, 600, Severity.Warning, all),
            Event("Windows critical event", "A Critical-level entry was written to the System or Application event log.", EventKeys.WindowsEvent, Severity.Major, all,
                new EventFilter { MinSeverity = Severity.Critical }),

            // ---- Phase 2: syslog and traps
            Event("Syslog emergency/alert/critical", "A device sent a syslog message with severity 0–2 (emergency, alert, critical).", EventKeys.SyslogMessage, Severity.Major, all,
                new EventFilter { MinSeverity = Severity.Critical }),
            Event("SNMP authentication failure", "A device reported an SNMP request with a wrong community or v3 credentials (authenticationFailure trap).", EventKeys.SnmpTrap, Severity.Warning, all,
                new EventFilter { Oid = "1.3.6.1.6.3.1.1.5.5" }),
            Count("Repeated login failures (syslog)", "At least 10 syslog messages about failed logins from one device within 5 minutes (possible brute force).",
                EventKeys.SyslogMessage, 10, 300, Severity.Warning, all,
                new EventFilter { Pattern = @"authentication fail|failed password|login fail|invalid user|bad password|failed login" }),

            // ---- Phase 3: VMware
            State("vCenter/ESXi API not reachable", "The vSphere API of a vCenter or ESXi host cannot be queried (credentials, certificate, network).", StateKeys.VcenterUnreachable, Severity.Major, 180, all),
            State("ESXi host not connected", "An ESXi host is disconnected or not responding in vCenter (and not in maintenance mode).", StateKeys.HostNotConnected, Severity.Critical, 0, all),
            State("ESXi host hardware alarm", "A hardware sensor of an ESXi host reports red (fan, power supply, temperature ...).", StateKeys.HostHardware, Severity.Major, 0, all),
            Threshold("ESXi host high CPU", MetricKeys.HostCpu, ComparisonOperator.GreaterThan, 90, 85, 300, Severity.Major, all),
            Threshold("ESXi host high memory", MetricKeys.HostMemory, ComparisonOperator.GreaterThan, 90, 85, 300, Severity.Major, all),
            Threshold("Datastore space low", MetricKeys.DatastoreUsed, ComparisonOperator.GreaterThan, 85, 80, 0, Severity.Warning, all),
            Threshold("Datastore space critical", MetricKeys.DatastoreUsed, ComparisonOperator.GreaterThan, 95, 92, 0, Severity.Critical, all),
            State("Datastore not accessible", "A datastore is not accessible from vCenter.", StateKeys.DatastoreInaccessible, Severity.Critical, 0, all),
            Threshold("Old snapshot", MetricKeys.SnapshotAgeDays, ComparisonOperator.GreaterThan, 7, null, 0, Severity.Warning, all),
            Threshold("Large snapshot", MetricKeys.SnapshotSizeGb, ComparisonOperator.GreaterThan, 50, 40, 0, Severity.Warning, all),
            Threshold("VM CPU ready high", MetricKeys.VmCpuReady, ComparisonOperator.GreaterThan, 10, 5, 600, Severity.Warning, all),
            State("VMware Tools not running", "A powered-on VM has no running VMware Tools (no guest heartbeat, IP or disk data).", StateKeys.VmToolsNotRunning, Severity.Warning, 900, all),
            State("vCenter alarm (red)", "A vCenter alarm is red and not acknowledged in vCenter.", StateKeys.VcenterAlarmRed, Severity.Major, 0, all),
            State("vCenter alarm (yellow)", "A vCenter alarm is yellow and not acknowledged in vCenter.", StateKeys.VcenterAlarmYellow, Severity.Warning, 0, all),
            Event("vCenter major event", "vCenter reported a major event: host connection lost, HA failover or isolation, failed VM reset ...", EventKeys.VmwareEvent, Severity.Major, all,
                new EventFilter { MinSeverity = Severity.Major }),

            // ---- Phase 4: domain controllers (Security log, replication, time)
            Count("Failed logons for one account", "At least 5 failed logons (4625/4771/4776) for the same account within 5 minutes (password guessing or a stale credential).",
                EventKeys.DcSecurity, 5, 300, Severity.Warning, all, new EventFilter { FieldEquals = new() { ["failed"] = "true" } }, "targetUser"),
            Count("Failed logons from one source", "At least 20 failed logons from the same source IP/workstation within 5 minutes (possible brute force or password spraying).",
                EventKeys.DcSecurity, 20, 300, Severity.Major, all, new EventFilter { FieldEquals = new() { ["failed"] = "true" } }, "source"),
            Event("Account locked out", "An account was locked out (4740).", EventKeys.DcSecurity, Severity.Major, all, new EventFilter { EventIds = [4740] }),
            Event("Privileged group membership added", "A member was added to a privileged group (default: Domain Admins, Enterprise Admins, Schema Admins, Administrators, Backup Operators, Account Operators; 4728/4732/4756).",
                EventKeys.DcSecurity, Severity.Critical, all, new EventFilter { EventIds = [4728, 4732, 4756], FieldEquals = new() { ["privileged"] = "true" } }),
            Event("Security log cleared", "The Security event log of a domain controller was cleared (1102).", EventKeys.DcSecurity, Severity.Critical, all, new EventFilter { EventIds = [1102] }),
            Event("Audit policy changed", "The audit policy of a domain controller was changed (4719).", EventKeys.DcSecurity, Severity.Critical, all, new EventFilter { EventIds = [4719] }),
            Event("Admin logon outside working hours", "Special privileges were assigned to a logon (4672) outside the configured working hours (default Mon–Fri 08:00–17:00).",
                EventKeys.DcSecurity, Severity.Warning, all, new EventFilter { EventIds = [4672], FieldEquals = new() { ["offHours"] = "true" } }),
            Event("New user account created", "A user account was created in the domain (4720).", EventKeys.DcSecurity, Severity.Warning, all, new EventFilter { EventIds = [4720] }),
            Event("New computer account created", "A computer account was created in the domain (4741).", EventKeys.DcSecurity, Severity.Warning, all, new EventFilter { EventIds = [4741] }),

            // ---- 2.0: account deletions and Kerberos pre-authentication spikes
            Event("User or computer account deleted", "A user account (4726) or computer account (4743) was deleted from the domain.", EventKeys.DcSecurity, Severity.Warning, all,
                new EventFilter { EventIds = [4726, 4743] }),
            Count("Kerberos pre-authentication failures spike", "At least 50 Kerberos pre-authentication failures (4771) on one DC within 5 minutes, across all accounts (password spraying or a widespread stale credential).",
                EventKeys.DcSecurity, 50, 300, Severity.Major, all, new EventFilter { EventIds = [4771] }),
            State("AD replication failing", "Replication with a partner DC has kept failing for 1 hour (per partner and naming context).", StateKeys.DcReplicationFailed, Severity.Major, 3600, all),
            State("AD replication stale", "No successful inbound replication from a partner for longer than the configured time (default 3 hours).", StateKeys.DcReplicationStale, Severity.Major, 0, all),
            State("DC service port unreachable", "LDAP (389), Kerberos (88) or DNS (53) of a domain controller did not accept TCP connections for 2 minutes.", StateKeys.DcPortUnreachable, Severity.Major, 120, all),
            Event("Directory/DFS replication error", "The Directory Service or DFS Replication log reported a replication problem (1311, 1864, 2042, 2087, 2088, 2213, 4012).",
                EventKeys.WindowsEvent, Severity.Major, all, new EventFilter { EventIds = [1311, 1864, 2042, 2087, 2088, 2213, 4012] }),
            Threshold("DC clock skew", MetricKeys.DcTimeSkew, ComparisonOperator.GreaterThan, 30, 20, 300, Severity.Warning, all),
            Threshold("DC clock skew critical", MetricKeys.DcTimeSkew, ComparisonOperator.GreaterThan, 120, 100, 0, Severity.Critical, all),

            // ---- 2.0: GPUs and server hardware (iLO / iDRAC)
            Threshold("GPU utilization high", MetricKeys.GpuUtilization, ComparisonOperator.GreaterThan, 95, 85, 600, Severity.Warning, all),
            Threshold("GPU temperature high", MetricKeys.GpuTemperature, ComparisonOperator.GreaterThan, 85, 80, 120, Severity.Major, all),
            Threshold("GPU memory high", MetricKeys.GpuMemory, ComparisonOperator.GreaterThan, 90, 85, 300, Severity.Warning, all),
            State("GPU missing or driver error", "A GPU is no longer detected, or its driver reports an error (device problem code, nvidia-smi failure).", StateKeys.GpuProblem, Severity.Major, 0, all),
            State("Server hardware component not OK", "iLO / iDRAC reports a component (fan, power supply, temperature, memory, processor, controller, drive) as Warning or Critical.", StateKeys.HardwareComponent, Severity.Major, 0, all),
            State("Management controller unreachable", "The server's iLO / iDRAC did not answer Redfish (or SNMP) for 15 minutes.", StateKeys.HardwareUnreachable, Severity.Warning, 900, all),

            // ---- Endpoints: GPO inventory script and forwarded workstation events
            State("Endpoint stopped reporting", "No inventory from a machine for longer than the configured time (default 48 hours; Endpoints → Deployment). The scheduled task may be removed, the machine off, or HTTPS to HIT SIEM blocked.",
                StateKeys.EndpointStale, Severity.Warning, 0, all),
            State("Endpoint firewall off", "A Windows Firewall profile (Domain, Private or Public) is turned off on a machine.", StateKeys.EndpointFirewallOff, Severity.Major, 0, all),
            State("Endpoint antivirus not protecting", "No antivirus is enabled, Microsoft Defender real-time protection is off, or the signatures are older than the configured days (default 7).",
                StateKeys.EndpointAntivirus, Severity.Major, 0, all),
            Threshold("Endpoint security updates missing", MetricKeys.EndpointMissingUpdates, ComparisonOperator.GreaterThan, 0, null, 7 * 86400, Severity.Warning, all),
            Threshold("Endpoint not patched for 45 days", MetricKeys.EndpointPatchAgeDays, ComparisonOperator.GreaterThan, 45, null, 0, Severity.Warning, all),
            Event("New software installed", "A program that was not there in the previous inventory appeared on a machine.", EventKeys.EndpointChange, Severity.Warning, all,
                new EventFilter { FieldEquals = new() { ["change"] = "softwareInstalled" } }),
            Event("New local administrator", "An account or group was added to the local Administrators group of a machine (inventory, or event 4732 forwarded).", EventKeys.EndpointChange, Severity.Major, all,
                new EventFilter { FieldEquals = new() { ["change"] = "localAdminAdded" } }),
            Event("Local administrator added (event)", "Event 4732 forwarded from a machine: a member was added to its local Administrators group.", EventKeys.EndpointEvent, Severity.Major, all,
                new EventFilter { EventIds = [4732], FieldEquals = new() { ["privileged"] = "true" } }),
            Event("USB storage connected", "A USB stick or external disk was connected to a machine (inventory, or event 6416 forwarded).", EventKeys.EndpointChange, Severity.Warning, all,
                new EventFilter { FieldEquals = new() { ["change"] = "usbStorage" } }),
            Event("USB storage connected (event)", "Event 6416 forwarded from a machine: a USB mass-storage device was recognised.", EventKeys.EndpointEvent, Severity.Warning, all,
                new EventFilter { EventIds = [6416], FieldEquals = new() { ["usbStorage"] = "true" } }),
            Event("Program or script file in user folders", "A new program, script, installer or disk image (.exe, .msi, .ps1, .bat, .js, .iso …) appeared in a user's Desktop, Downloads or Documents, or in a Startup folder.",
                EventKeys.EndpointChange, Severity.Warning, all, new EventFilter { FieldEquals = new() { ["change"] = "executableAdded" } }),
            Event("Hosts file changed", "The hosts file of a machine changed (it overrides DNS: used to redirect or block names).", EventKeys.EndpointChange, Severity.Major, all,
                new EventFilter { FieldEquals = new() { ["change"] = "hostsModified" } }),
            Event("Malware detected", "Microsoft Defender on a machine detected malware or took action against it (1116 / 1117).", EventKeys.EndpointEvent, Severity.Critical, all,
                new EventFilter { EventIds = [1116, 1117] }),
            Event("Suspicious command line", "A process was started with a command line typical for attacks: encoded PowerShell, credential dumping, shadow-copy deletion, log clearing, LOLBin downloads (4688).",
                EventKeys.EndpointEvent, Severity.Major, all, new EventFilter { FieldEquals = new() { ["suspicious"] = "true" } }),
            Event("New service installed on endpoint", "A new Windows service was installed on a machine (7045 / 4697).", EventKeys.EndpointEvent, Severity.Warning, all,
                new EventFilter { EventIds = [7045, 4697] }),
            Event("Endpoint event log cleared", "The Security (1102) or System (104) log of a machine was cleared.", EventKeys.EndpointEvent, Severity.Critical, all,
                new EventFilter { EventIds = [1102, 104] }),
            Event("Defender protection turned off", "Microsoft Defender real-time protection or scanning was turned off on a machine (5001 / 5010 / 5012).", EventKeys.EndpointEvent, Severity.Major, all,
                new EventFilter { EventIds = [5001, 5010, 5012] }),
            Count("Repeated failed logons on an endpoint", "At least 10 failed logons (4625) on one machine within 5 minutes.", EventKeys.EndpointEvent, 10, 300, Severity.Warning, all,
                new EventFilter { EventIds = [4625] }),

            // ---- Phase 5: self-monitoring
            State("HIT SIEM self-check failing", "A HIT SIEM self-check fails: data disk space, polling backlog, dropped samples or signals, message storm, certificate expiry, backups, scheduled reports, notifications or event forwarding.", StateKeys.SelfCheck, Severity.Major, 120, all),
        ];

        AlertRule Threshold(string name, string key, ComparisonOperator op, double threshold, double? clear, int durationSec, Severity severity, string scope) => new()
        {
            Name = name,
            Description = durationSec > 0
                ? $"{MetricKeys.DisplayNames.GetValueOrDefault(key, key)} above {threshold} for {durationSec / 60} min."
                : $"{MetricKeys.DisplayNames.GetValueOrDefault(key, key)} above {threshold}.",
            IsBuiltIn = true,
            IsEnabled = true,
            RuleType = AlertRuleType.Threshold,
            Key = key,
            Operator = op,
            Threshold = threshold,
            ClearThreshold = clear,
            DurationSec = durationSec,
            Severity = severity,
            ScopeJson = scope,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };

        AlertRule State(string name, string description, string key, Severity severity, int durationSec, string scope) => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsEnabled = true,
            RuleType = AlertRuleType.StateChange,
            Key = key,
            DurationSec = durationSec,
            Severity = severity,
            ScopeJson = scope,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };

        AlertRule Event(string name, string description, string key, Severity severity, string scope, EventFilter? filter) => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsEnabled = true,
            RuleType = AlertRuleType.EventMatch,
            Key = key,
            FilterJson = filter?.ToJson(),
            Severity = severity,
            ScopeJson = scope,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };

        AlertRule Count(string name, string description, string key, int count, int windowSec, Severity severity, string scope, EventFilter filter, string? groupBy = null) => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsEnabled = true,
            RuleType = AlertRuleType.CountInWindow,
            Key = key,
            FilterJson = filter.ToJson(),
            CountThreshold = count,
            WindowSec = windowSec,
            GroupByField = groupBy,
            Severity = severity,
            ScopeJson = scope,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };
    }
}
