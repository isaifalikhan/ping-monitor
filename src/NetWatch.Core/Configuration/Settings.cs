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
