using Microsoft.EntityFrameworkCore;
using NetWatch.Core.Entities;

namespace NetWatch.Data.Config;

/// <summary>Configuration, inventory, alerting and security data (netwatch.db).</summary>
public class NetWatchDbContext : DbContext
{
    public NetWatchDbContext(DbContextOptions<NetWatchDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();
    public DbSet<DeviceGroup> Groups => Set<DeviceGroup>();
    public DbSet<CredentialProfile> CredentialProfiles => Set<CredentialProfile>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceTag> DeviceTags => Set<DeviceTag>();
    public DbSet<DeviceCredential> DeviceCredentials => Set<DeviceCredential>();
    public DbSet<NetInterface> Interfaces => Set<NetInterface>();
    public DbSet<Sensor> Sensors => Set<Sensor>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AlertHistoryEntry> AlertHistory => Set<AlertHistoryEntry>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<ServerInfo> ServerInfos => Set<ServerInfo>();
    public DbSet<Volume> Volumes => Set<Volume>();
    public DbSet<WatchedService> WatchedServices => Set<WatchedService>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ModelConventions.Apply(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.Username).HasMaxLength(128).UseCollation("NOCASE");
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256);
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSessions");
            e.Property(x => x.Id).HasMaxLength(64);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresUtc);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.Property(x => x.Action).HasMaxLength(64);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<SettingEntry>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
        });

        b.Entity<DeviceGroup>(e =>
        {
            e.ToTable("Groups");
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ParentId, x.Name }).IsUnique();
        });

        b.Entity<CredentialProfile>(e =>
        {
            e.ToTable("CredentialProfiles");
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.DisplayHint).HasMaxLength(256);
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("Devices");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => x.IpAddress).IsUnique();
            e.HasIndex(x => x.GroupId);
            e.HasIndex(x => x.Type);
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.ParentDeviceId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Credentials).WithOne().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Interfaces).WithOne().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.WatchedServices).WithOne().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.SshHostKeyFingerprint).HasMaxLength(128);
        });

        b.Entity<ServerInfo>(e =>
        {
            e.ToTable("ServerInfo");
            e.HasKey(x => x.DeviceId);
            e.HasOne<Device>().WithOne().HasForeignKey<ServerInfo>(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.OsName).HasMaxLength(200);
            e.Property(x => x.OsVersion).HasMaxLength(100);
            e.Property(x => x.LastPatchId).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.EventLogError).HasMaxLength(1000);
        });

        b.Entity<Volume>(e =>
        {
            e.ToTable("Volumes");
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(260);
            e.Property(x => x.Label).HasMaxLength(200);
            e.Property(x => x.FileSystem).HasMaxLength(32);
            e.HasIndex(x => new { x.DeviceId, x.Name }).IsUnique();
            e.Ignore(x => x.UsedPct);
        });

        b.Entity<WatchedService>(e =>
        {
            e.ToTable("WatchedServices");
            e.Property(x => x.ServiceName).HasMaxLength(256).UseCollation("NOCASE");
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.StartMode).HasMaxLength(32);
            e.HasIndex(x => new { x.DeviceId, x.ServiceName }).IsUnique();
        });

        b.Entity<MaintenanceWindow>(e =>
        {
            e.ToTable("MaintenanceWindows");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.StartTimeLocal).HasMaxLength(5);
            e.Property(x => x.DaysOfWeek).HasMaxLength(40);
            e.Property(x => x.CronExpression).HasMaxLength(100);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.CreatedBy).HasMaxLength(128);
        });

        b.Entity<DeviceTag>(e =>
        {
            e.ToTable("DeviceTags");
            e.HasKey(x => new { x.DeviceId, x.Tag });
            e.Property(x => x.Tag).HasMaxLength(64).UseCollation("NOCASE");
            e.HasIndex(x => x.Tag);
        });

        b.Entity<DeviceCredential>(e =>
        {
            e.ToTable("DeviceCredentials");
            e.HasKey(x => new { x.DeviceId, x.Purpose });
            e.HasOne(x => x.CredentialProfile).WithMany().HasForeignKey(x => x.CredentialProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<NetInterface>(e =>
        {
            e.ToTable("Interfaces");
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.DeviceId, x.IfIndex }).IsUnique();
        });

        b.Entity<Sensor>(e =>
        {
            e.ToTable("Sensors");
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.DeviceId, x.Kind, x.SourceKey }).IsUnique();
        });

        b.Entity<AlertRule>(e =>
        {
            e.ToTable("AlertRules");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.FilterJson).HasMaxLength(2000);
        });

        b.Entity<Alert>(e =>
        {
            e.ToTable("Alerts");
            e.Property(x => x.DedupKey).HasMaxLength(200);
            e.HasOne<AlertRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.History).WithOne().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);

            // Deduplication: at most one open alert per rule+entity.
            e.HasIndex(x => x.DedupKey).IsUnique().HasFilter("\"State\" IN ('Active', 'Acknowledged')");
            e.HasIndex(x => new { x.State, x.Severity });
            e.HasIndex(x => x.DeviceId);
            e.HasIndex(x => x.FirstSeenUtc);
        });

        b.Entity<AlertHistoryEntry>(e =>
        {
            e.ToTable("AlertHistory");
            e.Property(x => x.Action).HasMaxLength(32);
        });
    }
}
