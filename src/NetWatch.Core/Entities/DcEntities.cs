namespace NetWatch.Core.Entities;

/// <summary>
/// A parsed Security-log event of a domain controller (logons, lockouts, account and group changes, audit policy).
/// Stored in events.db. Field names follow the Windows event schema (Subject = who did it, Target = whom it concerns).
/// </summary>
public class DcSecurityEvent
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int DeviceId { get; set; }
    public int EventId { get; set; }

    /// <summary>EventRecordID on the DC (unique per DC).</summary>
    public long RecordId { get; set; }
    public Severity Severity { get; set; }

    /// <summary>Short category: Logon, LogonFailure, Lockout, Account, Group, Kerberos, Policy, Directory.</summary>
    public string Category { get; set; } = string.Empty;
    public string? SubjectUser { get; set; }
    public string? SubjectDomain { get; set; }
    public string? TargetUser { get; set; }
    public string? TargetDomain { get; set; }

    /// <summary>Group name for membership changes (DOMAIN\Group).</summary>
    public string? GroupName { get; set; }

    /// <summary>Member DN/SID for group membership changes, object DN for 5136.</summary>
    public string? ObjectName { get; set; }
    public string? SourceIp { get; set; }
    public string? Workstation { get; set; }
    public int? LogonType { get; set; }

    /// <summary>NTSTATUS / Kerberos result code (hex), e.g. 0xC000006A (wrong password), 0x18 (pre-auth failed).</summary>
    public string? Status { get; set; }
    public string? SubStatus { get; set; }

    /// <summary>One-line human-readable summary.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Latest facts about a domain controller (one row per DC device).</summary>
public class DcInfo
{
    public int DeviceId { get; set; }
    public string? Domain { get; set; }
    public string? Forest { get; set; }
    public string? Site { get; set; }
    public string? DcName { get; set; }
    public bool IsGlobalCatalog { get; set; }

    /// <summary>FSMO roles held, comma separated (PdcRole, RidRole, InfrastructureRole, SchemaRole, NamingRole).</summary>
    public string? Roles { get; set; }
    public string? PdcEmulator { get; set; }

    /// <summary>DC clock minus NetWatch server clock, in seconds.</summary>
    public double? ClockOffsetSec { get; set; }

    /// <summary>Absolute clock difference to the PDC emulator, in seconds.</summary>
    public double? SkewToPdcSec { get; set; }
    public DateTime? LastReplicationCheckUtc { get; set; }
    public string? ReplicationError { get; set; }
    public string? SecurityLogError { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>Inbound replication from one partner for one naming context (as reported by the DC).</summary>
public class DcReplicationLink
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string Partner { get; set; } = string.Empty;
    public string NamingContext { get; set; } = string.Empty;
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }

    /// <summary>Win32 result of the last attempt (0 = success), e.g. 1722 RPC server unavailable, 8453 access denied.</summary>
    public int LastResult { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastMessage { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>A named filter a user saved on a page (e.g. the DC security event viewer).</summary>
public class SavedSearch
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Page { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Filter values as JSON (page specific).</summary>
    public string QueryJson { get; set; } = "{}";
    public bool IsShared { get; set; }
    public DateTime CreatedUtc { get; set; }
}
