using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetWatch.Core;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;

namespace NetWatch.Web.Auth;

/// <summary>Authenticated session as seen by request handlers.</summary>
public sealed record SessionInfo(string SessionId, int UserId, string Username, string DisplayName, UserRole Role, bool MustChangePassword);

/// <summary>
/// Server-side sessions. The cookie carries a random 256-bit token; only its SHA-256 is stored. Sessions expire
/// after an idle timeout and an absolute lifetime and can be revoked (logout, user disabled, role changed).
/// A short in-memory cache avoids a database hit on every request.
/// </summary>
public sealed class SessionService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CachedSession> _cache = new(StringComparer.Ordinal);

    private sealed class CachedSession
    {
        public required SessionInfo Info;
        public DateTime LastSeenUtc;
        public DateTime PersistedLastSeenUtc;
        public DateTime ExpiresUtc;
        public DateTime LoadedUtc;

        /// <summary>NOC kiosk session: no idle timeout (still revocable).</summary>
        public bool IsKiosk;
    }

    public SessionService(IServiceScopeFactory scopeFactory, SettingsService settings, TimeProvider time)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _time = time;
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Creates a session and returns the raw token for the cookie.</summary>
    public async Task<string> CreateAsync(User user, string? clientIp, string? userAgent, CancellationToken ct, bool kiosk = false)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = _time.GetUtcNow().UtcDateTime;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        db.UserSessions.Add(new UserSession
        {
            Id = HashToken(token),
            UserId = user.Id,
            CreatedUtc = now,
            LastSeenUtc = now,
            ExpiresUtc = kiosk ? now.AddYears(10) : now.AddHours(_settings.Security.SessionAbsoluteTimeoutHours),
            ClientIp = clientIp,
            UserAgent = userAgent is { Length: > 300 } ? userAgent[..300] : userAgent,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>Validates a token, sliding the idle timeout. Returns null if invalid/expired/revoked.</summary>
    public async Task<SessionInfo?> ValidateAsync(string token, CancellationToken ct)
    {
        var id = HashToken(token);
        var now = _time.GetUtcNow().UtcDateTime;
        var idle = TimeSpan.FromMinutes(_settings.Security.SessionIdleTimeoutMin);

        if (!_cache.TryGetValue(id, out var cached) || now - cached.LoadedUtc > CacheTtl)
        {
            cached = await LoadAsync(id, cached, now, ct);
            if (cached is null)
            {
                _cache.TryRemove(id, out _);
                return null;
            }

            _cache[id] = cached;
        }

        if ((!cached.IsKiosk && now - cached.LastSeenUtc > idle) || now >= cached.ExpiresUtc)
        {
            await RevokeAsync(token, ct);
            return null;
        }

        cached.LastSeenUtc = now;
        if (now - cached.PersistedLastSeenUtc > TouchInterval)
        {
            cached.PersistedLastSeenUtc = now;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            await db.UserSessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenUtc, now), ct);
        }

        return cached.Info;
    }

    private async Task<CachedSession?> LoadAsync(string id, CachedSession? previous, DateTime now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var row = await db.UserSessions.AsNoTracking().Include(s => s.User).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (row?.User is null || row.RevokedUtc is not null || !row.User.IsEnabled)
        {
            return null;
        }

        var lastSeen = previous is null ? row.LastSeenUtc : (previous.LastSeenUtc > row.LastSeenUtc ? previous.LastSeenUtc : row.LastSeenUtc);
        return new CachedSession
        {
            Info = new SessionInfo(id, row.UserId, row.User.Username, row.User.DisplayName, row.User.Role, row.User.MustChangePassword),
            LastSeenUtc = lastSeen,
            PersistedLastSeenUtc = row.LastSeenUtc,
            ExpiresUtc = row.ExpiresUtc,
            LoadedUtc = now,
            IsKiosk = row.User.AuthSource == AuthSource.Kiosk,
        };
    }

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        var id = HashToken(token);
        _cache.TryRemove(id, out _);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var now = _time.GetUtcNow().UtcDateTime;
        await db.UserSessions.Where(s => s.Id == id && s.RevokedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, now), ct);
    }

    /// <summary>Revokes all sessions of a user, optionally keeping one (e.g. the caller's own after a password change).</summary>
    public async Task RevokeUserAsync(int userId, string? exceptSessionId, CancellationToken ct)
    {
        foreach (var key in _cache.Where(kv => kv.Value.Info.UserId == userId && kv.Key != exceptSessionId).Select(kv => kv.Key).ToList())
        {
            _cache.TryRemove(key, out _);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var now = _time.GetUtcNow().UtcDateTime;
        await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedUtc == null && s.Id != exceptSessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, now), ct);
    }

    /// <summary>Drops cached session data of a user so changes (role, must-change-password) apply immediately.</summary>
    public void InvalidateUser(int userId)
    {
        foreach (var key in _cache.Where(kv => kv.Value.Info.UserId == userId).Select(kv => kv.Key).ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }
}
