using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NetWatch.Data.Config;
using NetWatch.Data.Events;

namespace NetWatch.Data;

/// <summary>Used only by "dotnet ef migrations" at development time.</summary>
internal sealed class NetWatchDbContextDesignFactory : IDesignTimeDbContextFactory<NetWatchDbContext>
{
    public NetWatchDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NetWatchDbContext>()
            .UseSqlite(SqliteConnectionFactory.Build("design-netwatch.db"))
            .Options);
}

/// <summary>Used only by "dotnet ef migrations" at development time.</summary>
internal sealed class EventsDbContextDesignFactory : IDesignTimeDbContextFactory<EventsDbContext>
{
    public EventsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<EventsDbContext>()
            .UseSqlite(SqliteConnectionFactory.Build("design-events.db"))
            .Options);
}
