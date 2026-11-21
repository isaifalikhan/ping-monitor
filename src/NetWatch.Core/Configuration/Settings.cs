namespace NetWatch.Core.Configuration;

/// <summary>Monitoring defaults. Stored in the Settings table under key "monitoring".</summary>
public sealed class MonitoringSettings
{
    public const string Key = "monitoring";

    public int DefaultPollIntervalSec { get; set; } = 60;

    /// <summary>A device is declared Down after this many consecutive failed polls.</summary>
    public int DownAfterFailedPolls { get; set; } = 3;
    public int PingCount { get; set; } = 3;
    public int PingTimeoutMs { get; set; } = 1000;
    public int SnmpTimeoutMs { get; set; } = 3000;
    public int SnmpRetries { get; set; } = 1;

    /// <summary>Maximum number of devices polled in parallel.</summary>
    public int MaxConcurrentPolls { get; set; } = 64;

    /// <summary>How often the interface table (names, speeds) is re-read.</summary>
    public int InterfaceMetadataRefreshMin { get; set; } = 30;

    /// <summary>How often vendor/model/serial/firmware are re-read.</summary>
    public int InventoryRefreshHours { get; set; } = 24;

    /// <summary>A port is flapping after this many up/down changes within <see cref="FlapWindowMin"/>.</summary>
    public int FlapChanges { get; set; } = 4;
    public int FlapWindowMin { get; set; } = 10;

    /// <summary>Read LLDP and CDP neighbours with the interface table (topology map, dependency suggestions).</summary>
    public bool DiscoverNeighbors { get; set; } = true;

    public void Normalise()
    {
        DefaultPollIntervalSec = Math.Clamp(DefaultPollIntervalSec, 15, 3600);
        DownAfterFailedPolls = Math.Clamp(DownAfterFailedPolls, 1, 20);
        PingCount = Math.Clamp(PingCount, 1, 10);
        PingTimeoutMs = Math.Clamp(PingTimeoutMs, 100, 5000);
        SnmpTimeoutMs = Math.Clamp(SnmpTimeoutMs, 500, 15000);
        SnmpRetries = Math.Clamp(SnmpRetries, 0, 5);
        MaxConcurrentPolls = Math.Clamp(MaxConcurrentPolls, 1, 512);
        InterfaceMetadataRefreshMin = Math.Clamp(InterfaceMetadataRefreshMin, 5, 1440);
        InventoryRefreshHours = Math.Clamp(InventoryRefreshHours, 1, 168);
        FlapChanges = Math.Clamp(FlapChanges, 2, 100);
        FlapWindowMin = Math.Clamp(FlapWindowMin, 2, 240);
    }
}

/// <summary>Console security settings. Stored under key "security".</summary>
public sealed class SecuritySettings
{
    public const string Key = "security";

    public int SessionIdleTimeoutMin { get; set; } = 30;
    public int SessionAbsoluteTimeoutHours { get; set; } = 12;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int MinPasswordLength { get; set; } = 10;

    public void Normalise()
    {
        SessionIdleTimeoutMin = Math.Clamp(SessionIdleTimeoutMin, 5, 24 * 60);
        SessionAbsoluteTimeoutHours = Math.Clamp(SessionAbsoluteTimeoutHours, 1, 24 * 7);
        MaxFailedLogins = Math.Clamp(MaxFailedLogins, 3, 50);
        LockoutMinutes = Math.Clamp(LockoutMinutes, 1, 24 * 60);
        MinPasswordLength = Math.Clamp(MinPasswordLength, 8, 128);
    }
}

/// <summary>Alert lifecycle: flapping protection, escalation, dependency suppression. Stored under key "alerting".</summary>
public sealed class AlertingSettings
{
    public const string Key = "alerting";

    /// <summary>An alert is "flapping" after this many raise/resolve changes within <see cref="FlapWindowMin"/>.</summary>
    public int FlapThreshold { get; set; } = 5;
    public int FlapWindowMin { get; set; } = 30;

    /// <summary>A flapping alert resolves only after its condition stayed clear this long.</summary>
    public int FlapStableMin { get; set; } = 15;

    /// <summary>Escalate unacknowledged Critical alerts after N minutes (0 = never).</summary>
    public int EscalateCriticalAfterMin { get; set; } = 15;

    /// <summary>Escalate unacknowledged Major alerts after N minutes (0 = never).</summary>
    public int EscalateMajorAfterMin { get; set; }

    /// <summary>Browsers repeat the alarm sound for escalated, unacknowledged alerts every N minutes (0 = once).</summary>
    public int RepeatSoundEveryMin { get; set; } = 5;

    /// <summary>Suppress alerts of devices whose upstream (parent) device is unreachable.</summary>
    public bool DependencySuppression { get; set; } = true;

    public void Normalise()
    {
        FlapThreshold = Math.Clamp(FlapThreshold, 3, 50);
        FlapWindowMin = Math.Clamp(FlapWindowMin, 5, 24 * 60);
        FlapStableMin = Math.Clamp(FlapStableMin, 1, 24 * 60);
        EscalateCriticalAfterMin = Math.Clamp(EscalateCriticalAfterMin, 0, 24 * 60);
        EscalateMajorAfterMin = Math.Clamp(EscalateMajorAfterMin, 0, 24 * 60);
        RepeatSoundEveryMin = Math.Clamp(RepeatSoundEveryMin, 0, 120);
    }
}

/// <summary>Syslog and SNMP trap listeners. Stored under key "receivers". Port changes need a service restart.</summary>
public sealed class ReceiverSettings
{
    public const string Key = "receivers";

    public bool SyslogEnabled { get; set; } = true;
    public int SyslogUdpPort { get; set; } = 514;
    public bool SyslogTcpEnabled { get; set; } = true;
    public int SyslogTcpPort { get; set; } = 514;
    public bool TrapsEnabled { get; set; } = true;
    public int TrapPort { get; set; } = 162;

    /// <summary>Keep messages from addresses that are not in the inventory (stored without a device).</summary>
    public bool AcceptUnknownSources { get; set; } = true;

    /// <summary>Upper bound of stored messages per second (syslog + traps); the rest is counted and dropped.</summary>
    public int MaxMessagesPerSecond { get; set; } = 2000;

    public void Normalise()
    {
        SyslogUdpPort = Math.Clamp(SyslogUdpPort, 1, 65535);
        SyslogTcpPort = Math.Clamp(SyslogTcpPort, 1, 65535);
        TrapPort = Math.Clamp(TrapPort, 1, 65535);
        MaxMessagesPerSecond = Math.Clamp(MaxMessagesPerSecond, 50, 50_000);
    }
}

/// <summary>Agentless server monitoring (WinRM/WMI, event logs, SSH). Stored under key "servers".</summary>
public sealed class ServerMonitoringSettings
{
    public const string Key = "servers";

    public int WinRmPort { get; set; } = 5985;

    /// <summary>Use WinRM over HTTPS (port 5986 by default). Requires an HTTPS listener on the servers.</summary>
    public bool WinRmHttps { get; set; }

    /// <summary>Fall back to WMI over DCOM when WinRM is not reachable.</summary>
    public bool DcomFallback { get; set; } = true;

    /// <summary>Read Critical/Error entries of the System and Application logs.</summary>
    public bool ReadEventLogs { get; set; } = true;

    /// <summary>Also store Warning-level entries.</summary>
    public bool IncludeWarnings { get; set; }

    /// <summary>Maximum event-log entries read per log and poll (protects against floods).</summary>
    public int MaxEventsPerPoll { get; set; } = 200;
    public int SshPort { get; set; } = 22;
    public int TimeoutSec { get; set; } = 20;

    public void Normalise()
    {
        WinRmPort = Math.Clamp(WinRmPort, 1, 65535);
        MaxEventsPerPoll = Math.Clamp(MaxEventsPerPoll, 10, 5000);
        SshPort = Math.Clamp(SshPort, 1, 65535);
        TimeoutSec = Math.Clamp(TimeoutSec, 5, 120);
    }
}

/// <summary>Domain controller monitoring (Security log, replication, time). Stored under key "dc".</summary>
public sealed class DcSettings
{
    public const string Key = "dc";

    /// <summary>Read the Security log of each DC remotely (account in "Event Log Readers").</summary>
    public bool ReadSecurityLog { get; set; } = true;

    /// <summary>
    /// Instead of remote reads, take DC Security events from this server's "ForwardedEvents" log (Windows Event
    /// Forwarding subscription). Remote Security-log reads are then skipped.
    /// </summary>
    public bool UseForwardedEvents { get; set; }

    /// <summary>
    /// DCs (name, short name or IP) that deliver their Security events through Windows Event Forwarding while the
    /// others are read remotely. Ignored when <see cref="UseForwardedEvents"/> is on (then every DC uses WEF).
    /// </summary>
    public List<string> ForwardedDcs { get; set; } = new();

    /// <summary>
    /// Sources (IP address, IPv4 CIDR such as 10.0.20.0/24, or workstation name) whose successful logons (4624)
    /// are not stored, e.g. application servers that log on over the network all day.
    /// </summary>
    public List<string> SuccessfulLogonIgnoreSources { get; set; } = new();

    /// <summary>True when any DC uses Windows Event Forwarding (the ForwardedEvents reader must run).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AnyForwarding => UseForwardedEvents || ForwardedDcs.Count > 0;

    /// <summary>True when this DC's Security events come from the ForwardedEvents log instead of remote reads.</summary>
    public bool UsesForwarding(string? name, string? ip)
    {
        if (UseForwardedEvents)
        {
            return true;
        }

        var shortName = name?.Split('.')[0];
        return ForwardedDcs.Any(d =>
            (name is not null && d.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            (shortName is not null && d.Split('.')[0].Equals(shortName, StringComparison.OrdinalIgnoreCase)) ||
            (ip is not null && d.Equals(ip, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>True when a successful logon from this source IP or workstation must not be stored.</summary>
    public bool IsIgnoredLogonSource(string? ip, string? workstation)
    {
        foreach (var entry in SuccessfulLogonIgnoreSources)
        {
            if (workstation is not null && entry.Equals(workstation, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ip is null || !System.Net.IPAddress.TryParse(ip, out var address))
            {
                continue;
            }

            if (entry.Contains('/'))
            {
                if (System.Net.IPNetwork.TryParse(entry, out var network) && network.Contains(address))
                {
                    return true;
                }
            }
            else if (System.Net.IPAddress.TryParse(entry, out var single) && single.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Store successful logons (4624). They are never alerted on by default.</summary>
    public bool StoreSuccessfulLogons { get; set; } = true;

    /// <summary>Logon types of 4624 to store (2 interactive, 3 network, 7 unlock, 10 RDP, 11 cached). Empty = all.</summary>
    public List<int> SuccessfulLogonTypes { get; set; } = [2, 7, 10, 11];

    /// <summary>Skip events about computer accounts (names ending in $) for 4624/4625/4768/4771/4776.</summary>
    public bool ExcludeComputerAccounts { get; set; } = true;
    public int MaxSecurityEventsPerPoll { get; set; } = 5000;

    /// <summary>Alert when a partner / naming context has not replicated successfully for this many hours.</summary>
    public int ReplicationStaleHours { get; set; } = 3;

    /// <summary>Check that LDAP (389), Kerberos (88) and DNS (53) accept TCP connections on every poll.</summary>
    public bool CheckServicePorts { get; set; } = true;

    /// <summary>How often replication partners are queried (minutes).</summary>
    public int ReplicationCheckMin { get; set; } = 5;

    /// <summary>Working days for "admin logon outside working hours" (Mon..Sun).</summary>
    public string WorkingDays { get; set; } = "Mon,Tue,Wed,Thu,Fri";

    /// <summary>Working hours in PKT ("HH:mm").</summary>
    public string WorkingHoursStart { get; set; } = "08:00";
    public string WorkingHoursEnd { get; set; } = "17:00";

    /// <summary>Groups whose new members raise a CRITICAL alert.</summary>
    public List<string> PrivilegedGroups { get; set; } = [.. DcDefaults.PrivilegedGroups];

    public bool IsPrivilegedGroup(string? group) =>
        group is { Length: > 0 } && PrivilegedGroups.Any(g => string.Equals(g, group.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Normalise()
    {
        PrivilegedGroups = PrivilegedGroups.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        ForwardedDcs = ForwardedDcs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        SuccessfulLogonIgnoreSources = SuccessfulLogonIgnoreSources.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();
        MaxSecurityEventsPerPoll = Math.Clamp(MaxSecurityEventsPerPoll, 100, 50_000);
        ReplicationCheckMin = Math.Clamp(ReplicationCheckMin, 1, 1440);
        ReplicationStaleHours = Math.Clamp(ReplicationStaleHours, 1, 24 * 14);
        SuccessfulLogonTypes = SuccessfulLogonTypes.Where(t => t is >= 0 and <= 13).Distinct().ToList();
        if (!TimeSpan.TryParseExact(WorkingHoursStart, @"hh\:mm", null, out _))
        {
            WorkingHoursStart = "08:00";
        }

        if (!TimeSpan.TryParseExact(WorkingHoursEnd, @"hh\:mm", null, out _))
        {
            WorkingHoursEnd = "17:00";
        }
    }

    /// <summary>True when <paramref name="utc"/> falls inside the working days and hours (PKT).</summary>
    public bool IsWorkingTime(DateTime utc)
    {
        var local = LocalTime.ToLocal(utc);
        var day = local.DayOfWeek.ToString()[..3];
        if (!WorkingDays.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(day, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var start = TimeSpan.TryParseExact(WorkingHoursStart, @"hh\:mm", null, out var s) ? s : TimeSpan.FromHours(8);
        var end = TimeSpan.TryParseExact(WorkingHoursEnd, @"hh\:mm", null, out var e) ? e : TimeSpan.FromHours(17);
        return local.TimeOfDay >= start && local.TimeOfDay < end;
    }
}

/// <summary>Server hardware (iLO / iDRAC over Redfish or SNMP) and GPU monitoring. Stored under key "hardware".</summary>
public sealed class HardwareSettings
{
    public const string Key = "hardware";

    /// <summary>How often the management controller is read (Redfish is slow; minutes).</summary>
    public int PollIntervalMin { get; set; } = 5;
    public int TimeoutSec { get; set; } = 30;

    /// <summary>Pin self-signed iLO/iDRAC certificates on first use (otherwise only CA-trusted certificates are accepted).</summary>
    public bool TrustOnFirstUse { get; set; } = true;

    /// <summary>Read GPU inventory, utilization and (NVIDIA) temperature/power from servers.</summary>
    public bool ReadGpus { get; set; } = true;

    /// <summary>Run nvidia-smi over WinRM / SSH for NVIDIA temperature, power and fan (needs remote shell rights).</summary>
    public bool RunNvidiaSmi { get; set; } = true;

    public void Normalise()
    {
        PollIntervalMin = Math.Clamp(PollIntervalMin, 1, 60);
        TimeoutSec = Math.Clamp(TimeoutSec, 5, 120);
    }
}

/// <summary>VMware (vCenter / ESXi) monitoring. Stored under key "vmware".</summary>
public sealed class VmwareSettings
{
    public const string Key = "vmware";

    /// <summary>Import vCenter events (VM powered off, host disconnected, HA failover ...).</summary>
    public bool ImportEvents { get; set; } = true;

    /// <summary>Import triggered vCenter alarms as NetWatch alerts.</summary>
    public bool ImportAlarms { get; set; } = true;

    /// <summary>Query the CPU-ready counter of powered-on VMs (one performance query per poll).</summary>
    public bool CollectCpuReady { get; set; } = true;

    /// <summary>
    /// When the vCenter certificate is not issued by a CA trusted on this server (VMCA self-signed), pin its
    /// SHA-256 thumbprint on the first connection and refuse a different one later. Off = such certificates are refused.
    /// </summary>
    public bool TrustOnFirstUse { get; set; } = true;
    public int TimeoutSec { get; set; } = 60;

    /// <summary>Maximum events read per poll.</summary>
    public int MaxEventsPerPoll { get; set; } = 500;

    public void Normalise()
    {
        TimeoutSec = Math.Clamp(TimeoutSec, 10, 300);
        MaxEventsPerPoll = Math.Clamp(MaxEventsPerPoll, 50, 5000);
    }
}

public enum SmtpSecurity
{
    None,
    StartTls,
    SslOnConnect
}

public enum ForwardProtocol
{
    Udp,
    Tcp
}

/// <summary>Offline notification channels besides the dashboard. Stored under key "notifications".</summary>
public sealed class NotificationSettings
{
    public const string Key = "notifications";

    public bool NotifyOnResolve { get; set; } = true;
    public bool NotifyOnEscalation { get; set; } = true;

    public bool EventLogEnabled { get; set; }
    public Severity EventLogMinSeverity { get; set; } = Severity.Warning;

    public bool SyslogForwardEnabled { get; set; }
    public string? SyslogForwardHost { get; set; }
    public int SyslogForwardPort { get; set; } = 514;
    public ForwardProtocol SyslogForwardProtocol { get; set; } = ForwardProtocol.Udp;
    public Severity SyslogForwardMinSeverity { get; set; } = Severity.Warning;

    public bool ScriptEnabled { get; set; }

    /// <summary>File name of a script in the data folder's "scripts" directory (.ps1, .cmd, .bat or .exe).</summary>
    public string? ScriptFile { get; set; }
    public Severity ScriptMinSeverity { get; set; } = Severity.Major;
    public int ScriptTimeoutSec { get; set; } = 30;

    public bool SmtpEnabled { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 25;
    public SmtpSecurity SmtpSecurity { get; set; } = SmtpSecurity.StartTls;
    public string? SmtpUsername { get; set; }

    /// <summary>SMTP password, DPAPI-protected and base64 encoded. Never returned by the API.</summary>
    public string? SmtpPasswordProtected { get; set; }
    public string? SmtpFrom { get; set; }

    /// <summary>Comma- or semicolon-separated recipients.</summary>
    public string? SmtpTo { get; set; }
    public Severity SmtpMinSeverity { get; set; } = Severity.Major;

    public void Normalise()
    {
        SyslogForwardPort = Math.Clamp(SyslogForwardPort, 1, 65535);
        ScriptTimeoutSec = Math.Clamp(ScriptTimeoutSec, 5, 300);
        SmtpPort = Math.Clamp(SmtpPort, 1, 65535);
    }
}

public enum LdapSecurity
{
    /// <summary>LDAP over TLS (port 636).</summary>
    Ldaps,
    /// <summary>LDAP with StartTLS (port 389).</summary>
    StartTls,
    /// <summary>Plain LDAP (port 389). Only allowed with Negotiate (Kerberos/NTLM signing and sealing).</summary>
    None
}

public enum LdapBindMethod
{
    /// <summary>Kerberos/NTLM via SSPI, with signing and sealing.</summary>
    Negotiate,
    /// <summary>Simple bind with user@domain; only over LDAPS or StartTLS.</summary>
    Simple
}

public sealed class LdapRoleMapping
{
    /// <summary>Distinguished name of an AD group (nested membership counts).</summary>
    public string GroupDn { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Viewer;
}

/// <summary>Sign-in with Active Directory accounts. Stored under key "ldap".</summary>
public sealed class LdapSettings
{
    public const string Key = "ldap";

    public bool Enabled { get; set; }

    /// <summary>Domain controllers (host names or IPs), tried in order.</summary>
    public List<string> Servers { get; set; } = new();
    public int Port { get; set; } = 636;
    public LdapSecurity Security { get; set; } = LdapSecurity.Ldaps;
    public LdapBindMethod BindMethod { get; set; } = LdapBindMethod.Negotiate;

    /// <summary>Validate the DC's TLS certificate (turn off only for testing).</summary>
    public bool ValidateCertificate { get; set; } = true;

    /// <summary>DNS domain, e.g. "corp.local" (used for user@domain and Negotiate).</summary>
    public string? Domain { get; set; }

    /// <summary>Search base, e.g. "DC=corp,DC=local".</summary>
    public string? BaseDn { get; set; }
    public int TimeoutSec { get; set; } = 10;

    /// <summary>The highest role of all matching groups is used; users in no mapped group cannot sign in.</summary>
    public List<LdapRoleMapping> RoleMappings { get; set; } = new();

    public void Normalise()
    {
        Port = Math.Clamp(Port, 1, 65535);
        TimeoutSec = Math.Clamp(TimeoutSec, 2, 60);
        Servers = Servers.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        RoleMappings = RoleMappings.Where(m => !string.IsNullOrWhiteSpace(m.GroupDn)).Take(50).ToList();
    }

    /// <summary>Returns a configuration error, or null when the settings are usable.</summary>
    public string? Validate()
    {
        if (!Enabled)
        {
            return null;
        }

        if (Servers.Count == 0)
        {
            return "Enter at least one domain controller.";
        }

        if (string.IsNullOrWhiteSpace(BaseDn))
        {
            return "Enter the search base (e.g. DC=corp,DC=local).";
        }

        if (string.IsNullOrWhiteSpace(Domain))
        {
            return "Enter the DNS domain (e.g. corp.local).";
        }

        if (BindMethod == LdapBindMethod.Simple && Security == LdapSecurity.None)
        {
            return "Simple bind without TLS would send passwords in clear text. Use LDAPS/StartTLS or Negotiate.";
        }

        if (RoleMappings.Count == 0)
        {
            return "Map at least one AD group to a role.";
        }

        return null;
    }
}

/// <summary>Data retention. Stored under key "retention".</summary>
public sealed class RetentionSettings
{
    public const string Key = "retention";

    public int RawDays { get; set; } = 7;
    public int FiveMinuteDays { get; set; } = 90;
    public int HourlyDays { get; set; } = 730;
    /// <summary>Device events, syslog, traps and Windows event-log entries.</summary>
    public int EventDays { get; set; } = 365;
    public int AuditDays { get; set; } = 365;
    public int ClosedAlertDays { get; set; } = 365;

    public void Normalise()
    {
        RawDays = Math.Clamp(RawDays, 1, 60);
        FiveMinuteDays = Math.Clamp(FiveMinuteDays, 7, 730);
        HourlyDays = Math.Clamp(HourlyDays, 30, 3650);
        EventDays = Math.Clamp(EventDays, 7, 3650);
        AuditDays = Math.Clamp(AuditDays, 30, 3650);
        ClosedAlertDays = Math.Clamp(ClosedAlertDays, 7, 3650);
    }
}

/// <summary>Automatic backups of the databases and configuration. Stored under key "backup".</summary>
public sealed class BackupSettings
{
    public const string Key = "backup";

    public bool Enabled { get; set; } = true;

    /// <summary>Daily backup time, local (PKT) "HH:mm".</summary>
    public string DailyAt { get; set; } = "01:30";

    /// <summary>Include the performance history (metrics.db, usually the largest file).</summary>
    public bool IncludeMetrics { get; set; } = true;

    /// <summary>Number of backup files kept; older ones are deleted after each successful backup.</summary>
    public int KeepCount { get; set; } = 14;

    /// <summary>Target folder (local path or UNC share); empty = the data folder's "backups" subfolder.</summary>
    public string? Folder { get; set; }

    public void Normalise()
    {
        KeepCount = Math.Clamp(KeepCount, 1, 365);
        if (!TimeSpan.TryParseExact(DailyAt, @"hh\:mm", null, out _))
        {
            DailyAt = "01:30";
        }

        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim();
    }

    /// <summary>Null when valid, otherwise an error text. Only absolute local or UNC paths are accepted.</summary>
    public string? ValidateFolder()
    {
        if (Folder is null)
        {
            return null;
        }

        if (Folder.Length > 240 || Folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(Folder) || Folder.Contains("..", StringComparison.Ordinal))
        {
            return @"The backup folder must be an absolute local path (D:\Backups\NetWatch) or a UNC share (\\server\share\netwatch).";
        }

        return null;
    }
}

/// <summary>Thresholds of NetWatch's self-monitoring. Stored under key "selfmonitor".</summary>
public sealed class SelfMonitorSettings
{
    public const string Key = "selfmonitor";

    /// <summary>Alert when free space on the data drive falls below this percentage …</summary>
    public int MinFreeDiskPct { get; set; } = 10;

    /// <summary>… or below this many GB.</summary>
    public int MinFreeDiskGb { get; set; } = 5;

    /// <summary>Alert when the server certificate expires within this many days.</summary>
    public int CertificateWarnDays { get; set; } = 30;

    /// <summary>Alert when the newest successful backup is older than this many hours (automatic backups on).</summary>
    public int MaxBackupAgeHours { get; set; } = 36;

    public void Normalise()
    {
        MinFreeDiskPct = Math.Clamp(MinFreeDiskPct, 1, 50);
        MinFreeDiskGb = Math.Clamp(MinFreeDiskGb, 1, 1000);
        CertificateWarnDays = Math.Clamp(CertificateWarnDays, 1, 365);
        MaxBackupAgeHours = Math.Clamp(MaxBackupAgeHours, 2, 24 * 31);
    }
}
