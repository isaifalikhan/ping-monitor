using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Servers;
using NetWatch.Core;
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
    private readonly ConcurrentDictionary<int, (CredentialKind Kind, CredentialSecret Secret)?> _cache = new();

    public CredentialResolver(IServiceScopeFactory scopeFactory, ISecretProtector protector, InventoryCache inventory, ILogger<CredentialResolver> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = protector;
        _logger = logger;
        inventory.Reloaded += _cache.Clear;
    }

    public async Task<SnmpCredential?> GetSnmpAsync(int profileId, CancellationToken ct) =>
        await GetAsync(profileId, ct) is { } c && c.Kind is CredentialKind.SnmpV2c or CredentialKind.SnmpV3
            ? new SnmpCredential(c.Kind, c.Secret)
            : null;

    /// <summary>Windows (WinRM/WMI, event logs) or SSH credential.</summary>
    public async Task<ManagementCredential?> GetManagementAsync(int profileId, CancellationToken ct) =>
        await GetAsync(profileId, ct) is { } c && c.Kind is CredentialKind.Windows or CredentialKind.Ssh
            ? new ManagementCredential(c.Kind, c.Secret)
            : null;

    /// <summary>vCenter / ESXi API credential.</summary>
    public async Task<ManagementCredential?> GetVcenterAsync(int profileId, CancellationToken ct) =>
        await GetAsync(profileId, ct) is { } c && c.Kind == CredentialKind.VCenter
            ? new ManagementCredential(c.Kind, c.Secret)
            : null;

    /// <summary>All SNMPv3 credentials (used by the trap receiver to decrypt v3 traps).</summary>
    public async Task<IReadOnlyList<CredentialSecret>> GetAllSnmpV3Async(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var ids = await db.CredentialProfiles.AsNoTracking().Where(p => p.Kind == CredentialKind.SnmpV3).Select(p => p.Id).ToListAsync(ct);
        var result = new List<CredentialSecret>();
        foreach (var id in ids)
        {
            if (await GetAsync(id, ct) is { } c)
            {
                result.Add(c.Secret);
            }
        }

        return result;
    }

    private async Task<(CredentialKind Kind, CredentialSecret Secret)?> GetAsync(int profileId, CancellationToken ct)
    {
        if (_cache.TryGetValue(profileId, out var cached))
        {
            return cached;
        }

        (CredentialKind, CredentialSecret)? result = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var profile = await db.CredentialProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, ct);
            if (profile is not null)
            {
                result = (profile.Kind, CredentialSecret.FromJson(_protector.Unprotect(profile.SecretBlob)));
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
