using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;

namespace NetWatch.Data.Config;

/// <summary>Typed access to the JSON settings stored in netwatch.db, with an in-memory cache.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, object> _cache = new();

    public SettingsService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public MonitoringSettings Monitoring => Get<MonitoringSettings>(MonitoringSettings.Key, s => s.Normalise());

    public SecuritySettings Security => Get<SecuritySettings>(SecuritySettings.Key, s => s.Normalise());

    public RetentionSettings Retention => Get<RetentionSettings>(RetentionSettings.Key, s => s.Normalise());

    public AlertingSettings Alerting => Get<AlertingSettings>(AlertingSettings.Key, s => s.Normalise());

    public ReceiverSettings Receivers => Get<ReceiverSettings>(ReceiverSettings.Key, s => s.Normalise());

    public ServerMonitoringSettings Servers => Get<ServerMonitoringSettings>(ServerMonitoringSettings.Key, s => s.Normalise());

    public NotificationSettings Notifications => Get<NotificationSettings>(NotificationSettings.Key, s => s.Normalise());

    public LdapSettings Ldap => Get<LdapSettings>(LdapSettings.Key, s => s.Normalise());

    public VmwareSettings Vmware => Get<VmwareSettings>(VmwareSettings.Key, s => s.Normalise());

    public HardwareSettings Hardware => Get<HardwareSettings>(HardwareSettings.Key, s => s.Normalise());

    public DcSettings Dc => Get<DcSettings>(DcSettings.Key, s => s.Normalise());

    public BackupSettings Backup => Get<BackupSettings>(BackupSettings.Key, s => s.Normalise());

    public EndpointSettings Endpoints => Get<EndpointSettings>(EndpointSettings.Key, s => s.Normalise());

    public SelfMonitorSettings SelfMonitor => Get<SelfMonitorSettings>(SelfMonitorSettings.Key, s => s.Normalise());

    public T Get<T>(string key, Action<T>? normalise = null)
        where T : class, new()
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return (T)cached;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var row = db.Settings.AsNoTracking().FirstOrDefault(s => s.Key == key);
        var value = row is null ? new T() : JsonSerializer.Deserialize<T>(row.ValueJson, JsonOptions) ?? new T();
        normalise?.Invoke(value);
        _cache[key] = value;
        return value;
    }

    public async Task SaveAsync<T>(string key, T value, int? userId, CancellationToken ct)
        where T : class
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            db.Settings.Add(new SettingEntry { Key = key, ValueJson = json, UpdatedUtc = DateTime.UtcNow, UpdatedByUserId = userId });
        }
        else
        {
            row.ValueJson = json;
            row.UpdatedUtc = DateTime.UtcNow;
            row.UpdatedByUserId = userId;
        }

        await db.SaveChangesAsync(ct);
        _cache[key] = value;
    }
}
