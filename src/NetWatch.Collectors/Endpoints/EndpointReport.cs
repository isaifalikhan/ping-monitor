namespace NetWatch.Collectors.Endpoints;

// The JSON document HitSiemInventory.ps1 sends (camelCase, every part optional: a section that failed on the
// machine is missing and named in Errors). Dates are ISO-8601 strings in UTC.

/// <summary>One inventory of one Windows machine, as sent by the GPO scheduled-task script.</summary>
public sealed class EndpointReport
{
    public int Schema { get; set; } = 1;
    public string? ScriptVersion { get; set; }
    public DateTime? CollectedUtc { get; set; }
    public double? DurationSec { get; set; }
    public EpSystem System { get; set; } = new();
    public List<EpAdapter> Adapters { get; set; } = new();
    public List<EpDisk> Disks { get; set; } = new();
    public List<EpProcess> Processes { get; set; } = new();
    public List<EpService> Services { get; set; } = new();
    public List<EpSoftware> Software { get; set; } = new();
    public List<EpHotfix> Hotfixes { get; set; } = new();
    public EpPatches? Patches { get; set; }
    public List<EpFirewallProfile> Firewall { get; set; } = new();
    public EpAntivirus? Antivirus { get; set; }
    public List<EpSession> Sessions { get; set; } = new();
    public List<EpAccount> LocalAdmins { get; set; } = new();
    public List<EpLocalUser> LocalUsers { get; set; } = new();
    public List<EpPort> Listening { get; set; } = new();
    public List<EpConnection> Connections { get; set; } = new();
    public List<EpUsb> Usb { get; set; } = new();
    public List<EpStartup> Startup { get; set; } = new();
    public List<EpShare> Shares { get; set; } = new();

    /// <summary>True when the file list below is complete enough to compare (collection on and no "files" error).</summary>
    public bool FilesCollected { get; set; }

    /// <summary>The machine had more files than the configured maximum; the list stops there.</summary>
    public bool FilesTruncated { get; set; }

    /// <summary>
    /// Files in every user's Desktop, Downloads and Documents, the Startup folders and the hosts file. Stored in
    /// the files table of endpoints.db, not in the compressed report.
    /// </summary>
    public List<EpFile> Files { get; set; } = new();

    /// <summary>Results of the remediation actions the machine carried out since its previous report.</summary>
    public List<EpActionResult> ActionResults { get; set; } = new();

    /// <summary>Section name → error text for the parts that could not be read on the machine.</summary>
    public Dictionary<string, string> Errors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class EpSystem
{
    public string? ComputerName { get; set; }
    public string? Domain { get; set; }
    public string? Fqdn { get; set; }
    public bool? PartOfDomain { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? BiosVersion { get; set; }
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? OsBuild { get; set; }
    public string? OsDisplayVersion { get; set; }
    public string? OsArchitecture { get; set; }

    /// <summary>Win32_OperatingSystem.ProductType: 1 workstation, 2 domain controller, 3 server.</summary>
    public int? ProductType { get; set; }
    public DateTime? InstallDateUtc { get; set; }
    public DateTime? LastBootUtc { get; set; }
    public string? CpuName { get; set; }
    public int? CpuCores { get; set; }
    public int? CpuLogical { get; set; }
    public double? CpuLoadPct { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public long? MemoryFreeBytes { get; set; }
    public string? TimeZone { get; set; }
    public bool? SecureBoot { get; set; }
    public bool? TpmPresent { get; set; }
    public string? ConsoleUser { get; set; }
}

public sealed class EpAdapter
{
    public string? Description { get; set; }
    public string? Mac { get; set; }
    public List<string> Ips { get; set; } = new();
    public List<string> Gateways { get; set; } = new();
    public List<string> Dns { get; set; } = new();
    public bool? Dhcp { get; set; }
}

public sealed class EpDisk
{
    public string? Drive { get; set; }
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public long? SizeBytes { get; set; }
    public long? FreeBytes { get; set; }

    /// <summary>BitLocker protection: "On", "Off" or null (unknown).</summary>
    public string? BitLocker { get; set; }
}

public sealed class EpProcess
{
    public int Pid { get; set; }
    public int? ParentPid { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? CommandLine { get; set; }
    public string? User { get; set; }
    public DateTime? StartUtc { get; set; }
    public double? CpuSec { get; set; }
    public long? MemoryBytes { get; set; }
    public string? Company { get; set; }
}

public sealed class EpService
{
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? State { get; set; }
    public string? StartMode { get; set; }
    public string? Account { get; set; }
    public string? Path { get; set; }
    public int? Pid { get; set; }
}

public sealed class EpSoftware
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Publisher { get; set; }

    /// <summary>Install date as written by the installer (usually yyyyMMdd).</summary>
    public string? InstallDate { get; set; }

    /// <summary>"Machine" or the user the per-user install belongs to.</summary>
    public string? Scope { get; set; }
    public string? Arch { get; set; }
}

public sealed class EpHotfix
{
    public string? Id { get; set; }
    public string? Description { get; set; }
    public DateTime? InstalledUtc { get; set; }
}

public sealed class EpPatches
{
    public bool? PendingReboot { get; set; }
    public int? MissingSecurity { get; set; }
    public int? MissingCritical { get; set; }
    public int? MissingOther { get; set; }
    public List<EpMissingUpdate> Missing { get; set; } = new();

    /// <summary>Last successful Windows Update scan of the machine itself (the script reads that scan's cached result).</summary>
    public DateTime? LastSearchUtc { get; set; }
    public DateTime? LastInstallUtc { get; set; }
    public string? WsusServer { get; set; }
    public string? Error { get; set; }
}

public sealed class EpMissingUpdate
{
    public string? Title { get; set; }
    public string? Kb { get; set; }
    public string? Severity { get; set; }
    public string? Category { get; set; }
}

public sealed class EpFirewallProfile
{
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? DefaultInbound { get; set; }
    public string? DefaultOutbound { get; set; }
}

public sealed class EpAntivirus
{
    public List<EpAvProduct> Products { get; set; } = new();
    public EpDefender? Defender { get; set; }
}

public sealed class EpAvProduct
{
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public bool? UpToDate { get; set; }
}

public sealed class EpDefender
{
    public bool? ServiceEnabled { get; set; }
    public bool? RealTimeProtection { get; set; }
    public DateTime? SignatureUtc { get; set; }
    public int? SignatureAgeDays { get; set; }
    public string? SignatureVersion { get; set; }
    public string? EngineVersion { get; set; }
    public bool? TamperProtected { get; set; }
    public DateTime? LastQuickScanUtc { get; set; }
    public DateTime? LastFullScanUtc { get; set; }
}

public sealed class EpSession
{
    public string? User { get; set; }
    public string? Session { get; set; }
    public string? State { get; set; }
    public DateTime? LogonUtc { get; set; }
}

public sealed class EpAccount
{
    public string? Name { get; set; }

    /// <summary>"User" or "Group".</summary>
    public string? Type { get; set; }

    /// <summary>"Local" or "Domain".</summary>
    public string? Source { get; set; }
}

public sealed class EpLocalUser
{
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public DateTime? LastLogonUtc { get; set; }
    public bool? PasswordRequired { get; set; }
}

public sealed class EpPort
{
    public string? Protocol { get; set; }
    public string? Address { get; set; }
    public int Port { get; set; }
    public int? Pid { get; set; }
    public string? Process { get; set; }
}

public sealed class EpConnection
{
    public string? Local { get; set; }
    public int LocalPort { get; set; }
    public string? Remote { get; set; }
    public int RemotePort { get; set; }
    public string? State { get; set; }
    public int? Pid { get; set; }
    public string? Process { get; set; }
}

public sealed class EpUsb
{
    public string? Name { get; set; }
    public string? DeviceId { get; set; }
    public string? Class { get; set; }

    /// <summary>USB mass storage (stick, external disk).</summary>
    public bool Storage { get; set; }

    /// <summary>Plugged in now (false = only in the machine's USB storage history).</summary>
    public bool Present { get; set; } = true;
    public string? Serial { get; set; }
}

public sealed class EpStartup
{
    public string? Name { get; set; }
    public string? Command { get; set; }
    public string? Location { get; set; }
    public string? User { get; set; }
}

public sealed class EpFile
{
    public string? Path { get; set; }

    /// <summary>Profile folder name ("jdoe"), "Public", "All users" or "System".</summary>
    public string? User { get; set; }

    /// <summary>Desktop, Downloads, Documents, Startup or System.</summary>
    public string? Folder { get; set; }
    public long Size { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public DateTime? CreatedUtc { get; set; }

    /// <summary>SHA-256 (upper-case hex); null for files over the size limit, locked files, or not hashed yet.</summary>
    public string? Sha256 { get; set; }
}

public sealed class EpShare
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Description { get; set; }
}

/// <summary>What a machine did with one remediation action.</summary>
public sealed class EpActionResult
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public DateTime? Utc { get; set; }
}
