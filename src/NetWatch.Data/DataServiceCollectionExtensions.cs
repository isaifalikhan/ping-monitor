using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Data.Metrics;
using NetWatch.Data.Security;

namespace NetWatch.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Registers DbContexts, the metric store, settings, audit and data background jobs.</summary>
    public static IServiceCollection AddNetWatchData(this IServiceCollection services, NetWatchPaths paths)
    {
        services.AddSingleton(paths);
        // Split queries: inventory loads devices with several child collections (tags, credentials, interfaces);
        // one JOIN per collection would multiply the rows returned.
        services.AddDbContext<NetWatchDbContext>(o => o.UseSqlite(
            SqliteConnectionFactory.Build(paths.ConfigDb),
            sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));
        services.AddDbContext<EventsDbContext>(o => o.UseSqlite(SqliteConnectionFactory.Build(paths.EventsDb)));

        services.AddSingleton<SqliteMetricStore>();
        services.AddSingleton<IMetricStore>(sp => sp.GetRequiredService<SqliteMetricStore>());
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AuditWriter>();
        services.AddSingleton(TimeProvider.System);

        services.AddHostedService<MetricWriterService>();
        services.AddHostedService<MetricRollupService>();
        services.AddHostedService<RetentionService>();
        services.AddSingleton<Backup.BackupService>();
        services.AddHostedService(sp => sp.GetRequiredService<Backup.BackupService>());
        return services;
    }

    /// <summary>
    /// Creates/upgrades all databases: WAL mode, EF migrations for netwatch.db and events.db, fixed tables in metrics.db.
    /// Called once at service start-up before hosted services run.
    /// </summary>
    public static async Task InitialiseDatabasesAsync(this IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        var paths = services.GetRequiredService<NetWatchPaths>();
        paths.EnsureCreated();

        foreach (var file in new[] { paths.ConfigDb, paths.EventsDb, paths.MetricsDb })
        {
            SqliteConnectionFactory.ConfigureDatabaseFile(file);
        }

        using var scope = services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var pending = (await config.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} configuration database migration(s): {Names}", pending.Count, string.Join(", ", pending));
        }

        await config.Database.MigrateAsync(ct);

        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
        await events.Database.MigrateAsync(ct);

        services.GetRequiredService<SqliteMetricStore>().Initialise();
    }
}
