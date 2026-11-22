using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Core;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;

namespace NetWatch.Tests;

/// <summary>
/// Release QA (TEST_PLAN SEC-32): session idle timeout (default 30 min), absolute timeout (default 12 h), activity
/// extends only the idle timer, kiosk (NOC) sessions never time out from idleness, revoked sessions end at once.
/// </summary>
public sealed class QaSessionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-qa-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(DateTime.UtcNow);
    private ServiceProvider _sp = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(new NetWatchPaths(_root));
        _sp = services.BuildServiceProvider();
        await _sp.InitialiseDatabasesAsync(NullLogger.Instance);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        _user = new User { Username = "qa-session", DisplayName = "QA", Role = UserRole.Operator, IsEnabled = true, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        db.Users.Add(_user);
        await db.SaveChangesAsync();
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

    private SessionService Sessions() => new(_sp.GetRequiredService<IServiceScopeFactory>(), _sp.GetRequiredService<SettingsService>(), _clock);

    [Fact]
    public async Task Idle_timeout_ends_an_unused_session_and_activity_keeps_it_alive()
    {
        var sessions = Sessions();
        var token = await sessions.CreateAsync(_user, "10.0.0.5", "qa", CancellationToken.None);
        Assert.NotNull(await sessions.ValidateAsync(token, CancellationToken.None));

        // Used every 25 minutes for 2 hours: stays valid.
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(25));
            Assert.NotNull(await sessions.ValidateAsync(token, CancellationToken.None));
        }

        // 31 minutes without a request: signed out — also for a new SessionService (after a service restart).
        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(await sessions.ValidateAsync(token, CancellationToken.None));
        Assert.Null(await Sessions().ValidateAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task Absolute_timeout_ends_even_an_active_session_and_revoke_is_immediate()
    {
        var sessions = Sessions();
        var token = await sessions.CreateAsync(_user, null, null, CancellationToken.None);
        for (var minutes = 0; minutes + 20 < 12 * 60; minutes += 20)
        {
            _clock.Advance(TimeSpan.FromMinutes(20));
            Assert.True(await sessions.ValidateAsync(token, CancellationToken.None) is not null, $"session ended after {minutes + 20} min of activity");
        }

        _clock.Advance(TimeSpan.FromMinutes(20)); // past 12 h since sign-in
        Assert.Null(await sessions.ValidateAsync(token, CancellationToken.None));

        var other = await sessions.CreateAsync(_user, null, null, CancellationToken.None);
        Assert.NotNull(await sessions.ValidateAsync(other, CancellationToken.None));
        await sessions.RevokeAsync(other, CancellationToken.None);
        Assert.Null(await sessions.ValidateAsync(other, CancellationToken.None));
        Assert.Null(await sessions.ValidateAsync("not-a-token", CancellationToken.None));
    }

    [Fact]
    public async Task Kiosk_sessions_have_no_idle_timeout()
    {
        // Kiosk mode belongs to the account (created by /api/kiosks with AuthSource Kiosk).
        User kioskUser;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            kioskUser = new User { Username = "kiosk:qa", DisplayName = "NOC wall", Role = UserRole.Viewer, AuthSource = AuthSource.Kiosk, IsEnabled = true, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
            db.Users.Add(kioskUser);
            await db.SaveChangesAsync();
        }

        var sessions = Sessions();
        var token = await sessions.CreateAsync(kioskUser, null, "wall", CancellationToken.None, kiosk: true);
        _clock.Advance(TimeSpan.FromDays(3));
        Assert.NotNull(await sessions.ValidateAsync(token, CancellationToken.None));
    }

    private sealed class Clock(DateTime start) : TimeProvider
    {
        private long _ticks = start.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
