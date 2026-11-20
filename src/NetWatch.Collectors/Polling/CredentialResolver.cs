using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Credentials;
using NetWatch.Data.Config;

namespace NetWatch.Collectors.Polling;

/// <summary>
/// Loads and decrypts credential profiles for polling. Decrypted secrets are cached in memory only, and the
/// cache is dropped whenever the inventory reloads (so edited credentials take effect within seconds).
/// </summary>
public sealed class CredentialResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecretProtector _protector;
    private readonly ILogger<CredentialResolver> _logger;
    private readonly ConcurrentDictionary<int, SnmpCredential?> _cache = new();

    public CredentialResolver(IServiceScopeFactory scopeFactory, ISecretProtector protector, InventoryCache inventory, ILogger<CredentialResolver> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = protector;
        _logger = logger;
        inventory.Reloaded += _cache.Clear;
    }

    public async Task<SnmpCredential?> GetSnmpAsync(int profileId, CancellationToken ct)
    {
        if (_cache.TryGetValue(profileId, out var cached))
        {
            return cached;
        }

        SnmpCredential? result = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var profile = await db.CredentialProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, ct);
            if (profile is not null && profile.Kind is Core.CredentialKind.SnmpV2c or Core.CredentialKind.SnmpV3)
            {
                result = new SnmpCredential(profile.Kind, CredentialSecret.FromJson(_protector.Unprotect(profile.SecretBlob)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most likely the database was restored on another machine (DPAPI keys differ).
            _logger.LogError(ex, "Cannot decrypt credential profile {ProfileId}; re-enter its secret in Settings", profileId);
        }

        _cache[profileId] = result;
        return result;
    }
}
