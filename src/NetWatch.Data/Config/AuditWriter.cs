using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Core.Entities;

namespace NetWatch.Data.Config;

/// <summary>Who performed an action (for the audit trail).</summary>
public sealed record AuditActor(int? UserId, string Username, string? ClientIp)
{
    public static readonly AuditActor System = new(null, "system", null);
}

/// <summary>Writes the audit trail. Failures are logged but never break the calling operation.</summary>
public sealed class AuditWriter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditWriter> _logger;

    public AuditWriter(IServiceScopeFactory scopeFactory, ILogger<AuditWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task WriteAsync(AuditActor actor, string action, string? targetType = null, string? targetId = null, string? details = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            db.AuditLog.Add(new AuditEntry
            {
                TimestampUtc = DateTime.UtcNow,
                UserId = actor.UserId,
                Username = actor.Username,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Details = details is { Length: > 4000 } ? details[..4000] : details,
                ClientIp = actor.ClientIp,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit entry {Action} by {User}", action, actor.Username);
        }
    }
}
