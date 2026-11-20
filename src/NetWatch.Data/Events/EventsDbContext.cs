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
    }
}
