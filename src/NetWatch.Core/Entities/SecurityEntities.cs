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
