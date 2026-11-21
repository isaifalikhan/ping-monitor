using Microsoft.EntityFrameworkCore;
using NetWatch.Core.Entities;

namespace NetWatch.Data.Events;

/// <summary>High-volume event data (events.db). Kept separate from configuration for write isolation and small config backups.</summary>
public class EventsDbContext : DbContext
{
    public EventsDbContext(DbContextOptions<EventsDbContext> options)
        : base(options)
    {
    }

    public DbSet<DeviceEvent> DeviceEvents => Set<DeviceEvent>();
    public DbSet<SyslogMessage> SyslogMessages => Set<SyslogMessage>();
    public DbSet<SnmpTrap> SnmpTraps => Set<SnmpTrap>();
    public DbSet<WindowsEventEntry> WindowsEvents => Set<WindowsEventEntry>();
    public DbSet<EventBookmark> EventBookmarks => Set<EventBookmark>();
    public DbSet<VmwareEvent> VmwareEvents => Set<VmwareEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ModelConventions.Apply(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DeviceEvent>(e =>
        {
            e.ToTable("DeviceEvents");
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.DeviceId, x.TimestampUtc });
        });

        b.Entity<SyslogMessage>(e =>
        {
            e.ToTable("SyslogMessages");
            e.Property(x => x.SourceIp).HasMaxLength(64);
            e.Property(x => x.Hostname).HasMaxLength(255);
            e.Property(x => x.AppName).HasMaxLength(64);
            e.Property(x => x.ProcId).HasMaxLength(64);
            e.Property(x => x.MsgId).HasMaxLength(64);
            e.Property(x => x.Message).HasMaxLength(8192);
            e.HasIndex(x => x.ReceivedUtc);
            e.HasIndex(x => new { x.DeviceId, x.ReceivedUtc });
            e.HasIndex(x => new { x.SyslogSeverity, x.ReceivedUtc });
        });

        b.Entity<SnmpTrap>(e =>
        {
            e.ToTable("SnmpTraps");
            e.Property(x => x.SourceIp).HasMaxLength(64);
            e.Property(x => x.Version).HasMaxLength(4);
            e.Property(x => x.TrapOid).HasMaxLength(200);
            e.Property(x => x.TrapName).HasMaxLength(100);
            e.Property(x => x.Summary).HasMaxLength(1000);
            e.HasIndex(x => x.ReceivedUtc);
            e.HasIndex(x => new { x.DeviceId, x.ReceivedUtc });
        });

        b.Entity<WindowsEventEntry>(e =>
        {
            e.ToTable("WindowsEvents");
            e.Property(x => x.LogName).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(8192);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.DeviceId, x.TimestampUtc });
            e.HasIndex(x => new { x.DeviceId, x.LogName, x.RecordId }).IsUnique();
        });

        b.Entity<VmwareEvent>(e =>
        {
            e.ToTable("VmwareEvents");
            e.Property(x => x.EventType).HasMaxLength(128);
            e.Property(x => x.UserName).HasMaxLength(255);
            e.Property(x => x.EntityName).HasMaxLength(255);
            e.Property(x => x.Message).HasMaxLength(4000);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.SourceDeviceId, x.EventKey }).IsUnique();
        });

        b.Entity<EventBookmark>(e =>
        {
            e.ToTable("EventBookmarks");
            e.HasKey(x => new { x.DeviceId, x.LogName });
            e.Property(x => x.LogName).HasMaxLength(64);
        });
    }
}
