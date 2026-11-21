namespace NetWatch.Core;

/// <summary>Kind of monitored device. Drives which collectors run and which UI pages show it.</summary>
public enum DeviceType
{
    Switch,
    Router,
    Firewall,
    Server,
    VM,
    DomainController,
    Hypervisor,
    VCenter,
    Other
}

/// <summary>Availability status computed by the collector.</summary>
public enum DeviceStatus
{
    Unknown,
    Up,
    Warning,
    Down,
    Maintenance
}

public enum OsKind
{
    Unknown,
    Windows,
    Linux,
    NetworkOs,
    Esxi
}

public enum CredentialKind
{
    SnmpV2c,
    SnmpV3,
    Windows,
    Ssh,
    VCenter,

    /// <summary>HPE iLO / Dell iDRAC Redfish API account (read-only role is enough).</summary>
    Redfish
}

/// <summary>What a credential is used for on a given device (a device can have one per purpose).</summary>
public enum CredentialPurpose
{
    Snmp,
    Management,
    VCenter,

    /// <summary>Baseboard management controller (iLO / iDRAC) Redfish account.</summary>
    Bmc
}

/// <summary>Roles are ordered: a higher value includes all rights of the lower ones.</summary>
public enum UserRole
{
    Viewer = 0,
    Operator = 1,
    Admin = 2
}

public enum AuthSource
{
    Local,
    Ldap,

    /// <summary>A NOC wall display signed in with a kiosk link (read-only, no password).</summary>
    Kiosk
}

public enum GroupKind
{
    Site,
    Building,
    Floor,
    Rack,
    Logical
}

public enum Severity
{
    Info = 0,
    Warning = 1,
    Major = 2,
    Critical = 3
}

public enum AlertState
{
    Active,
    Acknowledged,
    Resolved,
    Closed
}

/// <summary>Rule types supported by the alert engine.</summary>
public enum AlertRuleType
{
    /// <summary>Metric compared with a threshold, sustained for a duration.</summary>
    Threshold,
    /// <summary>A problem state (device down, port down, sensor failed) held for a duration.</summary>
    StateChange,
    /// <summary>A discrete event (e.g. device reboot, syslog message, trap) that raises an alert which needs acknowledgement.</summary>
    EventMatch,
    /// <summary>At least N matching events within a time window (e.g. repeated authentication failures).</summary>
    CountInWindow,
    /// <summary>No data received for a metric or event source for longer than the configured duration.</summary>
    Absence
}

/// <summary>How server health is collected (agentless).</summary>
public enum ServerCollectionMethod
{
    None,
    WinRm,
    Dcom,
    Ssh,
    Snmp
}

/// <summary>Windows service / systemd unit state as seen by the server collector.</summary>
public enum ServiceState
{
    Unknown,
    Running,
    Stopped,
    StartPending,
    StopPending,
    Paused,
    Failed,
    NotFound
}

/// <summary>Recurrence of a maintenance window.</summary>
public enum MaintenanceRecurrence
{
    /// <summary>One-off window between a start and an end time.</summary>
    Once,
    /// <summary>Every day at a local (PKT) time for a duration.</summary>
    Daily,
    /// <summary>On selected weekdays at a local (PKT) time for a duration.</summary>
    Weekly,
    /// <summary>Advanced: a cron expression (local time) and a duration.</summary>
    Cron
}

public enum ComparisonOperator
{
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    Equal,
    NotEqual
}

public enum SensorKind
{
    Temperature,
    Fan,
    PowerSupply,
    Other
}

public enum SensorState
{
    Unknown,
    Ok,
    Warning,
    Critical,
    NotPresent
}

public enum DeviceEventKind
{
    StatusChange,
    Reboot,
    InterfaceChange,
    Discovery,
    Inventory,
    Sensor,
    Service,
    Server
}

public enum SnmpAuthProtocol
{
    None,
    MD5,
    SHA1,
    SHA256,
    SHA384,
    SHA512
}

public enum SnmpPrivProtocol
{
    None,
    DES,
    AES128,
    AES192,
    AES256
}
