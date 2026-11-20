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

/// <summary>Data retention. Stored under key "retention".</summary>
public sealed class RetentionSettings
{
    public const string Key = "retention";

    public int RawDays { get; set; } = 7;
    public int FiveMinuteDays { get; set; } = 90;
    public int HourlyDays { get; set; } = 730;
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
