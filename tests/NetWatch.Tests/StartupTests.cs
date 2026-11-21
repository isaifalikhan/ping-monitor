using Microsoft.EntityFrameworkCore;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Data.Events;

namespace NetWatch.Tests;

/// <summary>
/// Guards the upgrade path: every model change needs a migration, and the real host must boot on an empty data
/// folder (databases, seeding, simulator, all hosted services, Kestrel) and stop cleanly.
/// </summary>
[Collection(nameof(StartupTests))]
public sealed class StartupTests
{
    [Fact]
    public void Config_model_has_no_pending_changes()
    {
        using var db = new NetWatchDbContext(new DbContextOptionsBuilder<NetWatchDbContext>()
            .UseSqlite(SqliteConnectionFactory.Build(":memory:")).Options);
        Assert.False(db.Database.HasPendingModelChanges(), "NetWatchDbContext changed without a migration: run dotnet ef migrations add.");
    }

    [Fact]
    public void Events_model_has_no_pending_changes()
    {
        using var db = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>()
            .UseSqlite(SqliteConnectionFactory.Build(":memory:")).Options);
        Assert.False(db.Database.HasPendingModelChanges(), "EventsDbContext changed without a migration: run dotnet ef migrations add.");
    }

    [Fact]
    public async Task Host_boots_on_empty_data_folder_and_stops_cleanly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netwatch-startup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exit = await NetWatch.Service.Program.Main(
                ["--self-test", "--simulate", "--data-dir", dir, "--Https:Port=0", "--SelfTest:Seconds=3"]);
            Assert.Equal(0, exit);
            Assert.True(File.Exists(Path.Combine(dir, "data", "netwatch.db")) || Directory.EnumerateFiles(dir, "netwatch.db", SearchOption.AllDirectories).Any());
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // SQLite WAL files may still be closing; the temp folder is cleaned up by the OS later.
            }
        }
    }
}
