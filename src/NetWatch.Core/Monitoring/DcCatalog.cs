namespace NetWatch.Core;

/// <summary>Defaults for domain controller monitoring.</summary>
public static class DcDefaults
{
    /// <summary>Services watched on a new domain controller (AD DS, DNS, Netlogon, Kerberos KDC, DFS Replication, Windows Time).</summary>
    public static readonly IReadOnlyList<string> WatchedServices = ["NTDS", "DNS", "Netlogon", "Kdc", "DFSR", "W32Time"];

    /// <summary>Groups whose membership changes are critical (matched case-insensitively on the group name).</summary>
    public static readonly IReadOnlyList<string> PrivilegedGroups =
        ["Domain Admins", "Enterprise Admins", "Schema Admins", "Administrators", "Backup Operators"];
}

/// <summary>Category of a collected DC Security-log event.</summary>
public enum DcEventCategory
{
    Logon,
    LogonFailure,
    Lockout,
    Account,
    Group,
    Kerberos,
    Policy,
    Directory
}

/// <summary>The Security-log event IDs NetWatch collects from domain controllers, with category and display name.</summary>
public static class DcEventCatalog
{
    public static readonly IReadOnlyDictionary<int, (DcEventCategory Category, string Name)> Events = new Dictionary<int, (DcEventCategory, string)>
    {
        [4624] = (DcEventCategory.Logon, "Successful logon"),
        [4625] = (DcEventCategory.LogonFailure, "Failed logon"),
        [4740] = (DcEventCategory.Lockout, "Account locked out"),
        [4767] = (DcEventCategory.Lockout, "Account unlocked"),
        [4720] = (DcEventCategory.Account, "User account created"),
        [4722] = (DcEventCategory.Account, "User account enabled"),
        [4725] = (DcEventCategory.Account, "User account disabled"),
        [4726] = (DcEventCategory.Account, "User account deleted"),
        [4738] = (DcEventCategory.Account, "User account changed"),
        [4723] = (DcEventCategory.Account, "Password change attempt"),
        [4724] = (DcEventCategory.Account, "Password reset attempt"),
        [4741] = (DcEventCategory.Account, "Computer account created"),
        [4743] = (DcEventCategory.Account, "Computer account deleted"),
        [4728] = (DcEventCategory.Group, "Member added to global security group"),
        [4729] = (DcEventCategory.Group, "Member removed from global security group"),
        [4732] = (DcEventCategory.Group, "Member added to local security group"),
        [4733] = (DcEventCategory.Group, "Member removed from local security group"),
        [4756] = (DcEventCategory.Group, "Member added to universal security group"),
        [4757] = (DcEventCategory.Group, "Member removed from universal security group"),
        [4672] = (DcEventCategory.Logon, "Special privileges assigned to logon"),
        [4768] = (DcEventCategory.Kerberos, "Kerberos TGT requested"),
        [4771] = (DcEventCategory.Kerberos, "Kerberos pre-authentication failed"),
        [4776] = (DcEventCategory.Kerberos, "NTLM credential validation"),
        [4719] = (DcEventCategory.Policy, "Audit policy changed"),
        [1102] = (DcEventCategory.Policy, "Security log cleared"),
        [5136] = (DcEventCategory.Directory, "Directory object modified"),
    };

    /// <summary>Event IDs that add a member to a group.</summary>
    public static readonly IReadOnlySet<int> GroupAdds = new HashSet<int> { 4728, 4732, 4756 };

    /// <summary>Directory Service / DFS Replication event IDs that indicate replication problems.</summary>
    public static readonly IReadOnlySet<int> ReplicationEventIds = new HashSet<int> { 1311, 1864, 2042, 2087, 2088, 2213, 4012 };

    public static string NameOf(int eventId) => Events.TryGetValue(eventId, out var e) ? e.Name : $"Event {eventId}";

    /// <summary>Default severity of a stored event (before rules).</summary>
    public static Severity SeverityOf(int eventId, bool failed, bool privileged) => eventId switch
    {
        1102 or 4719 => Severity.Critical,
        4740 => Severity.Major,
        _ when privileged && GroupAdds.Contains(eventId) => Severity.Critical,
        4720 or 4726 or 4741 or 4743 or 4728 or 4732 or 4756 or 4724 => Severity.Warning,
        _ when failed => Severity.Warning,
        _ => Severity.Info,
    };

    /// <summary>Win32 / NTSTATUS failure text for the most common logon status codes.</summary>
    public static string? StatusText(string? status) => status?.ToLowerInvariant() switch
    {
        "0xc000006a" => "wrong password",
        "0xc0000064" => "unknown user name",
        "0xc000006d" => "bad user name or password",
        "0xc0000234" => "account locked out",
        "0xc0000072" => "account disabled",
        "0xc000006f" => "logon outside allowed hours",
        "0xc0000070" => "workstation restriction",
        "0xc0000071" => "password expired",
        "0xc0000193" => "account expired",
        "0xc0000224" => "password must change",
        "0xc000015b" => "logon type not granted",
        "0x18" => "wrong password (Kerberos)",
        "0x12" => "account disabled, expired or locked (Kerberos)",
        "0x17" => "password expired (Kerberos)",
        "0x6" => "unknown user (Kerberos)",
        _ => null,
    };
}
