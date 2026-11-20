using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Snmp;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Credentials;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;
using NetWatch.Web.Infrastructure;

namespace NetWatch.Web.Api;

/// <summary>Groups (sites tree) and credential profiles.</summary>
public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/groups", ListGroupsAsync).RequireAuthorization(Policies.Viewer);

        var admin = app.MapGroup("/api").RequireAuthorization(Policies.Admin);
        admin.MapPost("/groups", CreateGroupAsync);
        admin.MapPut("/groups/{id:int}", UpdateGroupAsync);
        admin.MapDelete("/groups/{id:int}", DeleteGroupAsync);

        admin.MapGet("/credentials", ListCredentialsAsync);
        admin.MapPost("/credentials", CreateCredentialAsync);
        admin.MapPut("/credentials/{id:int}", UpdateCredentialAsync);
        admin.MapDelete("/credentials/{id:int}", DeleteCredentialAsync);
        admin.MapPost("/credentials/{id:int}/test", TestCredentialAsync);
    }

    // ---------------------------------------------------------------- groups

    private static async Task<IResult> ListGroupsAsync(NetWatchDbContext db, CancellationToken ct)
    {
        var counts = await db.Devices.Where(d => d.GroupId != null).GroupBy(d => d.GroupId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(ct);
        return Results.Ok(groups.Select(g => new GroupDto(g.Id, g.ParentId, g.Name, g.Kind, g.SortOrder, counts.GetValueOrDefault(g.Id))));
    }

    private static async Task<IResult> CreateGroupAsync(GroupInput input, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var group = new DeviceGroup();
        if (await ApplyGroupAsync(group, input, db, ct) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "GroupCreated", "Group", group.Id.Inv(), group.Name);
        return Results.Created($"/api/groups/{group.Id}", new { group.Id });
    }

    private static async Task<IResult> UpdateGroupAsync(int id, GroupInput input, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
        {
            return Results.NotFound();
        }

        if (await ApplyGroupAsync(group, input, db, ct) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        await db.SaveChangesAsync(ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "GroupUpdated", "Group", id.Inv(), group.Name);
        return Results.NoContent();
    }

    private static async Task<string?> ApplyGroupAsync(DeviceGroup group, GroupInput input, NetWatchDbContext db, CancellationToken ct)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 128)
        {
            return "Group name is required (max 128 characters).";
        }

        if (input.ParentId is { } parentId)
        {
            var all = await db.Groups.AsNoTracking().ToListAsync(ct);
            if (all.All(g => g.Id != parentId))
            {
                return "Parent group does not exist.";
            }

            // Prevent cycles: the new parent must not be the group itself or one of its descendants.
            if (group.Id != 0 && DeviceEndpoints.Subtree(all, group.Id).Contains(parentId))
            {
                return "A group cannot be moved under itself or one of its sub-groups.";
            }
        }

        if (await db.Groups.AnyAsync(g => g.ParentId == input.ParentId && g.Name == name && g.Id != group.Id, ct))
        {
            return "A group with this name already exists at this level.";
        }

        group.Name = name;
        group.ParentId = input.ParentId;
        group.Kind = input.Kind;
        group.SortOrder = input.SortOrder;
        return null;
    }

    private static async Task<IResult> DeleteGroupAsync(int id, HttpContext http, NetWatchDbContext db, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
        {
            return Results.NotFound();
        }

        if (await db.Groups.AnyAsync(g => g.ParentId == id, ct))
        {
            return HttpExtensions.Error("Delete or move the sub-groups first.");
        }

        // Devices of the group become ungrouped (FK set null).
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "GroupDeleted", "Group", id.Inv(), group.Name);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- credentials

    private static async Task<IResult> ListCredentialsAsync(NetWatchDbContext db, CancellationToken ct)
    {
        var usage = await db.DeviceCredentials.GroupBy(c => c.CredentialProfileId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var rows = await db.CredentialProfiles.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

        // SecretBlob is never returned.
        return Results.Ok(rows.Select(c => new CredentialDto(c.Id, c.Name, c.Kind, c.DisplayHint, usage.GetValueOrDefault(c.Id), c.UpdatedUtc)));
    }

    private static CredentialSecret ToSecret(CredentialInput input) => new()
    {
        Community = input.Community,
        Username = input.Username?.Trim(),
        Password = input.Password,
        Domain = input.Domain?.Trim(),
        AuthProtocol = input.AuthProtocol,
        AuthPassword = input.AuthPassword,
        PrivProtocol = input.PrivProtocol,
        PrivPassword = input.PrivPassword,
        ContextName = input.ContextName,
        PrivateKey = input.PrivateKey,
    };

    private static async Task<IResult> CreateCredentialAsync(CredentialInput input, HttpContext http, NetWatchDbContext db, ISecretProtector protector, AuditWriter audit, CancellationToken ct)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 128)
        {
            return HttpExtensions.Error("Name is required (max 128 characters).");
        }

        if (await db.CredentialProfiles.AnyAsync(c => c.Name == name, ct))
        {
            return HttpExtensions.Error("A credential profile with this name already exists.");
        }

        var secret = ToSecret(input);
        if (secret.Validate(input.Kind) is { } error)
        {
            return HttpExtensions.Error(error);
        }

        var now = DateTime.UtcNow;
        var profile = new CredentialProfile
        {
            Name = name,
            Kind = input.Kind,
            DisplayHint = secret.BuildDisplayHint(input.Kind),
            SecretBlob = protector.Protect(secret.ToJson()),
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.CredentialProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(http.Actor(), "CredentialCreated", "Credential", profile.Id.Inv(), $"{profile.Name} ({profile.Kind})");
        return Results.Created($"/api/credentials/{profile.Id}", new { profile.Id });
    }

    private static async Task<IResult> UpdateCredentialAsync(int id, CredentialInput input, HttpContext http, NetWatchDbContext db, ISecretProtector protector, IInventoryChangeNotifier notifier, AuditWriter audit, CancellationToken ct)
    {
        var profile = await db.CredentialProfiles.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (profile is null)
        {
            return Results.NotFound();
        }

        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 128)
        {
            return HttpExtensions.Error("Name is required (max 128 characters).");
        }

        if (await db.CredentialProfiles.AnyAsync(c => c.Name == name && c.Id != id, ct))
        {
            return HttpExtensions.Error("A credential profile with this name already exists.");
        }

        profile.Name = name;
        if (input.ReplaceSecret)
        {
            var secret = ToSecret(input);
            if (secret.Validate(input.Kind) is { } error)
            {
                return HttpExtensions.Error(error);
            }

            profile.Kind = input.Kind;
            profile.DisplayHint = secret.BuildDisplayHint(input.Kind);
            profile.SecretBlob = protector.Protect(secret.ToJson());
        }

        profile.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Reloading the inventory also drops cached decrypted secrets.
        notifier.NotifyInventoryChanged();
        await audit.WriteAsync(http.Actor(), "CredentialUpdated", "Credential", id.Inv(), $"{profile.Name}; secret replaced: {input.ReplaceSecret}");
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteCredentialAsync(int id, HttpContext http, NetWatchDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var profile = await db.CredentialProfiles.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (profile is null)
        {
            return Results.NotFound();
        }

        var used = await db.DeviceCredentials.CountAsync(c => c.CredentialProfileId == id, ct);
        if (used > 0)
        {
            return HttpExtensions.Error($"This profile is used by {used} device(s). Assign another profile to them first.");
        }

        db.CredentialProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(http.Actor(), "CredentialDeleted", "Credential", id.Inv(), profile.Name);
        return Results.NoContent();
    }

    private static async Task<IResult> TestCredentialAsync(int id, CredentialTestRequest request, HttpContext http, NetWatchDbContext db, ISecretProtector protector, SettingsService settings, IServerCollector servers, AuditWriter audit, CancellationToken ct)
    {
        if (!System.Net.IPAddress.TryParse(request.IpAddress?.Trim(), out var ip))
        {
            return HttpExtensions.Error("Enter a valid IP address to test against.");
        }

        var profile = await db.CredentialProfiles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (profile is null)
        {
            return Results.NotFound();
        }

        var secret = CredentialSecret.FromJson(protector.Unprotect(profile.SecretBlob));
        if (profile.Kind is CredentialKind.Windows or CredentialKind.Ssh)
        {
            await audit.WriteAsync(http.Actor(), "CredentialTested", "Credential", id.Inv(), ip.ToString());
            var s = settings.Servers;
            var data = await servers.CollectAsync(new ServerPollRequest(
                ip.ToString(), new ManagementCredential(profile.Kind, secret), Array.Empty<string>(), new Dictionary<string, long>(),
                null, false, false, 10, s.WinRmHttps && s.WinRmPort == 5985 ? 5986 : s.WinRmPort, s.WinRmHttps, s.DcomFallback, s.SshPort,
                TimeSpan.FromSeconds(s.TimeoutSec)), ct);
            return data.Ok
                ? Results.Ok(new { success = true, sysName = data.OsName, sysDescription = $"{data.OsName} {data.OsVersion} via {data.Method}", vendor = (string?)null, model = (string?)null, method = data.Method.ToString() })
                : Results.Ok(new { success = false, error = data.Error });
        }

        if (profile.Kind is not (CredentialKind.SnmpV2c or CredentialKind.SnmpV3))
        {
            return HttpExtensions.Error("This credential type cannot be tested yet (vCenter arrives in Phase 3).");
        }
        var monitoring = settings.Monitoring;
        var client = new SnmpClient(ip, new Collectors.Polling.SnmpCredential(profile.Kind, secret), monitoring.SnmpTimeoutMs, monitoring.SnmpRetries);
        await audit.WriteAsync(http.Actor(), "CredentialTested", "Credential", id.Inv(), ip.ToString());
        try
        {
            var info = await SystemInfoProbe.ProbeAsync(client, ct);
            return Results.Ok(new { success = true, info.SysName, info.SysDescription, info.SysObjectId, info.Vendor, info.Model });
        }
        catch (Exception ex) when (ex is SnmpException or TimeoutException or OperationCanceledException)
        {
            return Results.Ok(new { success = false, error = ex is OperationCanceledException ? "Timed out" : ex.Message });
        }
    }
}
