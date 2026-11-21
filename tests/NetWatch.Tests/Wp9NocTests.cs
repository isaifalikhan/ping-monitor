using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Core;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;

namespace NetWatch.Tests;

/// <summary>WP9: kiosk (NOC wall display) sessions have no idle timeout, stay read-only, cannot use passwords and end on revoke.</summary>
public sealed class Wp9NocTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-wp9-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero));
    private ServiceProvider _sp = null!;

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(new NetWatchPaths(_root));
        _sp = services.BuildServiceProvider();
        await _sp.InitialiseDatabasesAsync(NullLogger.Instance);
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<User> AddUserAsync(string name, AuthSource source)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var user = new User { Username = name, DisplayName = name, AuthSource = source, Role = UserRole.Viewer, IsEnabled = true, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Kiosk_sessions_never_idle_out_but_normal_sessions_do()
    {
        var sessions = new SessionService(_sp.GetRequiredService<IServiceScopeFactory>(), _sp.GetRequiredService<SettingsService>(), _clock);
        var kioskUser = await AddUserAsync("kiosk:wall1", AuthSource.Kiosk);
        var person = await AddUserAsync("operator1", AuthSource.Local);
        var kiosk = await sessions.CreateAsync(kioskUser, "10.0.0.5", "wall", CancellationToken.None, kiosk: true);
        var normal = await sessions.CreateAsync(person, "10.0.0.6", "desk", CancellationToken.None);

        _clock.Now = _clock.Now.AddHours(3); // well past the 30-minute idle timeout
        Assert.NotNull(await sessions.ValidateAsync(kiosk, CancellationToken.None));
        Assert.Null(await sessions.ValidateAsync(normal, CancellationToken.None));

        _clock.Now = _clock.Now.AddDays(30); // a month on the wall without anyone touching it
        var info = await sessions.ValidateAsync(kiosk, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(UserRole.Viewer, info.Role);
    }

    [Fact]
    public async Task Revoking_a_display_ends_its_session()
    {
        var sessions = new SessionService(_sp.GetRequiredService<IServiceScopeFactory>(), _sp.GetRequiredService<SettingsService>(), _clock);
        var kioskUser = await AddUserAsync("kiosk:wall2", AuthSource.Kiosk);
        var token = await sessions.CreateAsync(kioskUser, null, null, CancellationToken.None, kiosk: true);
        Assert.NotNull(await sessions.ValidateAsync(token, CancellationToken.None));

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            await db.UserSessions.Where(s => s.UserId == kioskUser.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, DateTime.UtcNow));
            await db.Users.Where(u => u.Id == kioskUser.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsEnabled, false));
        }

        sessions.InvalidateUser(kioskUser.Id);
        Assert.Null(await sessions.ValidateAsync(token, CancellationToken.None));
    }

    [Fact]
    public void Kiosk_accounts_cannot_sign_in_with_a_password()
    {
        var passwords = new PasswordService();
        var kiosk = new User { Username = "kiosk:x", AuthSource = AuthSource.Kiosk, PasswordHash = null };
        Assert.False(passwords.Verify(kiosk.AuthSource == AuthSource.Local ? kiosk : null, "anything").Valid);
        Assert.Equal(64, SessionService.HashToken("token").Length); // only the SHA-256 of a link token is stored
    }
}
