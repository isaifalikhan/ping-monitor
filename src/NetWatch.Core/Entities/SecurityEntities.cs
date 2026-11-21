namespace NetWatch.Core.Entities;

/// <summary>A user of the NetWatch web console (local or AD/LDAP backed).</summary>
public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }

    /// <summary>PBKDF2 hash (ASP.NET Core Identity v3 format). Null for LDAP users.</summary>
    public string? PasswordHash { get; set; }

    public AuthSource AuthSource { get; set; } = AuthSource.Local;
    public UserRole Role { get; set; } = UserRole.Viewer;
    public bool IsEnabled { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Server-side session record. The browser cookie only carries an opaque token whose SHA-256 is <see cref="Id"/>.</summary>
/// <summary>A NOC wall display: a revocable link that signs a browser in as a read-only kiosk account.</summary>
public class KioskDisplay
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the link token; the token itself is shown once and never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The kiosk account (AuthSource Kiosk, role Viewer) the display signs in as.</summary>
    public int UserId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
}

public class UserSession
{
    public string Id { get; set; } = string.Empty;
    public int UserId { get; set; }
    public User? User { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime? RevokedUtc { get; set; }
}

/// <summary>Immutable audit trail of user actions (logins, configuration changes, acknowledgements).</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int? UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public string? ClientIp { get; set; }
}

/// <summary>Key/value JSON settings (monitoring, security, retention ...).</summary>
public class SettingEntry
{
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public DateTime UpdatedUtc { get; set; }
    public int? UpdatedByUserId { get; set; }
}
