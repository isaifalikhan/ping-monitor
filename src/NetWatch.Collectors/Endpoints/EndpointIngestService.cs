using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Inventory;
using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;
using NetWatch.Data.Config;

namespace NetWatch.Collectors.Endpoints;

public enum IngestStatus
{
    Ok,
    Disabled,
    Unauthorized,
    Conflict,
    TooSoon,
    Invalid,
}

/// <param name="NewToken">Set when the machine enrolled: the script stores it and sends it from then on.</param>
/// <param name="Actions">Remediation actions the machine should carry out (handed out with the answer).</param>
public sealed record IngestResult(IngestStatus Status, string Message, string? NewToken = null, int? EndpointId = null, int? DeviceId = null, int Changes = 0, IReadOnlyList<EndpointAction>? Actions = null);

/// <param name="MachineId">Random ID the script created on the machine (HKLM, readable by SYSTEM and administrators only).</param>
/// <param name="Simulated">Report from the built-in simulator: a created device gets the "simulated" tag.</param>
public sealed record IngestRequest(string? MachineId, string? Token, string? EnrollmentKey, string? RemoteIp, EndpointReport Report, bool Simulated = false);

/// <summary>
/// Accepts one inventory from the GPO script: checks the machine's token (or the enrollment key on first contact),
/// links the machine to its inventory device (creating a Workstation device when needed), records the changes
/// since the previous inventory and publishes the alert signals.
/// </summary>
public sealed partial class EndpointIngestService
{
    /// <summary>Tag of devices created from an endpoint inventory.</summary>
    public const string EndpointTag = "endpoint";

    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan NameTakenWindow = TimeSpan.FromHours(24);

    private readonly EndpointStore _store;
    private readonly InventoryCache _inventory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertSignalSink _alerts;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<EndpointIngestService> _logger;
    private readonly SemaphoreSlim _deviceLock = new(1, 1);

    public EndpointIngestService(EndpointStore store, InventoryCache inventory, IServiceScopeFactory scopeFactory, IAlertSignalSink alerts, SettingsService settings, TimeProvider time, ILogger<EndpointIngestService> logger)
    {
        _store = store;
        _inventory = inventory;
        _scopeFactory = scopeFactory;
        _alerts = alerts;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    public long ReportsAccepted { get; private set; }
    public long ReportsRefused { get; private set; }
    public DateTime? LastReportUtc { get; private set; }

    public async Task<IngestResult> IngestAsync(IngestRequest request, CancellationToken ct)
    {
        var result = await IngestCoreAsync(request, ct);
        if (result.Status == IngestStatus.Ok)
        {
            ReportsAccepted++;
            LastReportUtc = _time.GetUtcNow().UtcDateTime;
        }
        else
        {
            ReportsRefused++;
            if (result.Status is IngestStatus.Unauthorized or IngestStatus.Conflict)
            {
                _logger.LogWarning("Endpoint report from {Ip} ({Machine}) refused: {Reason}", request.RemoteIp, request.Report.System.ComputerName, result.Message);
            }
        }

        return result;
    }

    private async Task<IngestResult> IngestCoreAsync(IngestRequest request, CancellationToken ct)
    {
        var settings = _settings.Endpoints;
        if (!settings.Enabled)
        {
            return new IngestResult(IngestStatus.Disabled, "Endpoint inventory is turned off in HIT SIEM (Endpoints → Deployment).");
        }

        var report = request.Report;
        var name = report.System.ComputerName?.Trim().ToUpperInvariant();
        if (request.MachineId is not { } machineId || !MachineIdPattern().IsMatch(machineId))
        {
            return new IngestResult(IngestStatus.Invalid, "Missing or invalid machine ID.");
        }

        if (name is null || !ComputerNamePattern().IsMatch(name))
        {
            return new IngestResult(IngestStatus.Invalid, "Missing or invalid computer name.");
        }

        var domain = string.IsNullOrWhiteSpace(report.System.Domain) ? null : report.System.Domain.Trim().ToLowerInvariant();
        EndpointDiff.Sanitize(report);
        var now = _time.GetUtcNow().UtcDateTime;

        var row = _store.FindByMachine(machineId);
        string? newToken = null;
        if (row?.TokenHash is { } hash)
        {
            if (!EndpointStore.TokenMatches(request.Token, hash))
            {
                return new IngestResult(IngestStatus.Unauthorized,
                    "This machine ID is enrolled with a different token. If the machine was reinstalled or its HIT SIEM registry key was copied, use Endpoints → machine → Reset enrollment.");
            }

            if (now - row.LastSeenUtc < MinInterval)
            {
                return new IngestResult(IngestStatus.TooSoon, "Report received less than a minute ago.");
            }
        }
        else
        {
            if (!_store.IsEnrollmentKey(request.EnrollmentKey))
            {
                return new IngestResult(IngestStatus.Unauthorized, "Wrong or missing enrollment key (download a new deployment kit after the key was changed).");
            }

            var others = _store.FindByName(name, domain, machineId);
            if (others.FirstOrDefault(o => now - o.LastSeenUtc < NameTakenWindow) is { } active)
            {
                return new IngestResult(IngestStatus.Conflict,
                    $"Another machine named {name} reported in the last 24 hours (last at {active.LastSeenUtc:yyyy-MM-dd HH:mm} UTC). Remove the old entry in HIT SIEM if this machine replaced it.");
            }

            // The old entry of a reinstalled machine (same name, silent for a day) is replaced.
            foreach (var old in others)
            {
                await _store.DeleteAsync(old.Id);
            }

            newToken = EndpointStore.NewSecret(32);
            await _store.EnrollAsync(machineId, name, domain, EndpointStore.HashToken(newToken), now);
            row = _store.FindByMachine(machineId)!;
        }

        var previous = _store.Report(row.Id);
        var ip = EndpointDiff.PrimaryIp(report, request.RemoteIp);
        var device = await LinkDeviceAsync(row, name, report, ip, settings, request.Simulated, now, ct);

        var changes = EndpointDiff.Compare(previous, report);
        if (previous is null)
        {
            changes.Insert(0, (ChangeKinds.Enrolled, name, Join(report.System.OsName, report.System.OsBuild)));
        }

        // Files: compared with the stored list; the first list from a machine is the baseline (no changes).
        FileDelta? fileDelta = null;
        if (report.FilesCollected)
        {
            var (delta, fileChanges) = EndpointDiff.Files(_store.FileState(row.Id), report.Files, baseline: row.FileCount is null);
            fileDelta = delta;
            changes.AddRange(fileChanges);
        }

        var summary = new EndpointSummary(
            name, domain, Cut(report.System.Fqdn, 255), ip, Cut(report.ScriptVersion, 32), Cut(report.System.OsName, 200), Cut(report.System.OsBuild, 64),
            Cut(Join(report.System.Manufacturer, report.System.Model), 200), Cut(report.System.SerialNumber, 100), Cut(EndpointDiff.User(report), 200),
            EndpointDiff.FirewallOff(report), EndpointDiff.AvProblem(report, settings.AvSignatureMaxAgeDays), EndpointDiff.MissingUpdates(report),
            EndpointDiff.LastPatch(report), report.Patches?.PendingReboot, EndpointDiff.WorstDiskPct(report),
            report.Errors.Count == 0 ? null : Cut(string.Join("; ", report.Errors.Select(kv => $"{kv.Key}: {kv.Value}")), 2000));
        await _store.SaveReportAsync(row.Id, device?.Id, report, summary, changes, now, fileDelta);

        if (report.ActionResults.Count > 0)
        {
            await _store.RecordActionResultsAsync(row.Id, report.ActionResults.Where(a => a.Id > 0).Take(100).ToList(), now);
        }

        if (device is not null)
        {
            Publish(device, name, report, summary, changes, now);
        }

        // Hand out queued remediation actions only while remediation is enabled.
        var actions = settings.AllowRemediation ? await _store.TakePendingActionsAsync(row.Id, now) : null;
        return new IngestResult(IngestStatus.Ok, newToken is null ? "Inventory received." : "Machine enrolled, inventory received.", newToken, row.Id, device?.Id, changes.Count, actions);
    }

    private void Publish(DeviceSnapshot device, string name, EndpointReport report, EndpointSummary summary, List<(string Kind, string Item, string? Detail)> changes, DateTime now)
    {
        _alerts.Publish(new StateSignal(device.Id, null, StateKeys.EndpointStale, null, false, $"Inventory received from {name}", now));

        foreach (var p in report.Firewall.Where(p => p.Name is "Domain" or "Private" or "Public" && p.Enabled is not null))
        {
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.EndpointFirewallOff, p.Name, p.Enabled == false,
                p.Enabled == false ? $"Windows Firewall {p.Name} profile is off on {name}" : $"Windows Firewall {p.Name} profile is on", now, $"{p.Name} profile"));
        }

        if (report.Antivirus is not null)
        {
            _alerts.Publish(new StateSignal(device.Id, null, StateKeys.EndpointAntivirus, null, summary.AvProblem is not null,
                summary.AvProblem is { } problem ? $"{problem} on {name}" : "Antivirus protecting", now));
        }

        if (summary.MissingUpdates is { } missing)
        {
            _alerts.Publish(new MetricSignal(device.Id, null, MetricKeys.EndpointMissingUpdates, null, missing, now));
        }

        if (summary.LastPatchUtc is { } lastPatch)
        {
            _alerts.Publish(new MetricSignal(device.Id, null, MetricKeys.EndpointPatchAgeDays, null, Math.Floor((now - lastPatch).TotalDays), now));
        }

        // Disks of machines HIT SIEM does not already read over WMI or SNMP.
        if (device.Type == DeviceType.Workstation || (device.ManagementCredentialId is null && device.SnmpCredentialId is null))
        {
            foreach (var d in report.Disks.Where(d => d.SizeBytes > 0 && d.FreeBytes is not null && d.Drive is not null))
            {
                var used = Math.Round(100.0 * (d.SizeBytes!.Value - d.FreeBytes!.Value) / d.SizeBytes.Value, 1);
                _alerts.Publish(new MetricSignal(device.Id, null, MetricKeys.DiskUsed, d.Drive, used, now, d.Drive));
                _alerts.Publish(new MetricSignal(device.Id, null, MetricKeys.DiskFreeGb, d.Drive, Math.Round(d.FreeBytes.Value / 1073741824.0, 2), now, d.Drive));
            }
        }

        foreach (var (kind, item, detail) in changes.Where(c => EndpointDiff.Alertable.Contains(c.Kind)))
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["change"] = kind,
                ["item"] = item,
                ["endpoint"] = name,
            };
            if (detail is not null)
            {
                fields["detail"] = detail;
            }

            _alerts.Publish(new EventSignal(device.Id, EventKeys.EndpointChange, EndpointDiff.Message(name, kind, item, detail), now,
                kind is ChangeKinds.LocalAdminAdded or ChangeKinds.FirewallDisabled or ChangeKinds.HostsModified ? Severity.Major : Severity.Warning, "Endpoint inventory", null, null, fields));
        }
    }

    /// <summary>
    /// Finds (or creates) the inventory device of the machine: the linked device, else a device with the same
    /// name or host name, else a non-workstation device with the same IP (servers with fixed addresses), else a
    /// new Workstation (or Server) device. Fills empty hardware fields and keeps a workstation's IP current.
    /// </summary>
    private async Task<DeviceSnapshot?> LinkDeviceAsync(EndpointRow row, string name, EndpointReport report, string? ip, Core.Configuration.EndpointSettings settings, bool simulated, DateTime now, CancellationToken ct)
    {
        await _deviceLock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var fqdn = report.System.Fqdn?.Trim();
            Device? device = row.DeviceId is { } linked ? await db.Devices.Include(d => d.Tags).FirstOrDefaultAsync(d => d.Id == linked, ct) : null;
            if (device is null)
            {
                var match = _inventory.All.FirstOrDefault(d => NameMatches(d.Name, name, fqdn) || NameMatches(d.Hostname, name, fqdn))
                    ?? (ip is not null && System.Net.IPAddress.TryParse(ip, out var address) && _inventory.FindByIp(address) is { Type: not DeviceType.Workstation } byIp ? byIp : null);
                if (match is not null)
                {
                    device = await db.Devices.Include(d => d.Tags).FirstOrDefaultAsync(d => d.Id == match.Id, ct);
                }
            }

            var created = false;
            var inventoryChanged = false;
            if (device is null)
            {
                if (!settings.AutoCreateDevices)
                {
                    return null;
                }

                device = new Device
                {
                    Name = name,
                    Hostname = Cut(fqdn, 255),
                    Type = report.System.ProductType is 2 or 3 ? DeviceType.Server : DeviceType.Workstation,
                    Os = OsKind.Windows,
                    IpAddress = await FreeIpAsync(db, ip, null, ct),
                    PollIntervalSec = _settings.Monitoring.DefaultPollIntervalSec,
                    Status = DeviceStatus.Up,
                    StatusSinceUtc = now,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    Notes = "Created from the endpoint inventory (GPO script).",
                };
                device.Tags.Add(new DeviceTag { Tag = EndpointTag });
                if (simulated)
                {
                    device.Tags.Add(new DeviceTag { Tag = DeviceSnapshot.SimulatedTag });
                }

                db.Devices.Add(device);
                created = true;
            }

            var sys = report.System;
            device.Hostname ??= Cut(fqdn, 255);
            device.Vendor ??= Cut(sys.Manufacturer, 100);
            device.Model ??= Cut(sys.Model, 100);
            device.SerialNumber ??= Cut(sys.SerialNumber, 100);
            if (device.Os == OsKind.Unknown)
            {
                device.Os = OsKind.Windows;
            }

            if (device.Type == DeviceType.Workstation)
            {
                if (device.Status != DeviceStatus.Up)
                {
                    device.Status = DeviceStatus.Up;
                    device.StatusSinceUtc = now;
                }

                device.LastPollUtc = now;
                device.Firmware = Cut(Join(sys.OsName, sys.OsBuild), 100);
                if (!created && ip is not null && device.IpAddress != ip)
                {
                    var newIp = await FreeIpAsync(db, ip, device.Id, ct);
                    if (newIp != device.IpAddress && !(newIp.StartsWith("0.", StringComparison.Ordinal) && device.IpAddress.StartsWith("0.", StringComparison.Ordinal)))
                    {
                        device.IpAddress = newIp;
                        inventoryChanged = true;
                    }
                }
            }

            if (db.ChangeTracker.HasChanges())
            {
                device.UpdatedUtc = now;
                await db.SaveChangesAsync(ct);
            }

            if (created)
            {
                // The alert engine needs the device in the inventory before the first signals.
                await _inventory.ReloadAsync(ct);
            }
            else if (inventoryChanged)
            {
                _inventory.NotifyInventoryChanged();
            }

            return _inventory.GetSnapshot(device.Id);
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <summary>
    /// The IP to store for a workstation. When another workstation still has that (DHCP) address, the other one
    /// moves to a placeholder in 0.0.0.0/8 ("address unknown"); an address held by any other device type is never
    /// taken, the workstation gets the placeholder instead.
    /// </summary>
    private static async Task<string> FreeIpAsync(NetWatchDbContext db, string? ip, int? selfId, CancellationToken ct)
    {
        if (ip is not null)
        {
            var holder = await db.Devices.FirstOrDefaultAsync(d => d.IpAddress == ip && d.Id != selfId, ct);
            if (holder is null)
            {
                return ip;
            }

            if (holder.Type == DeviceType.Workstation)
            {
                holder.IpAddress = await PlaceholderAsync(db, ct);
                return ip;
            }
        }

        return await PlaceholderAsync(db, ct);
    }

    private static async Task<string> PlaceholderAsync(NetWatchDbContext db, CancellationToken ct)
    {
        while (true)
        {
            var b = System.Security.Cryptography.RandomNumberGenerator.GetBytes(3);
            var candidate = string.Create(CultureInfo.InvariantCulture, $"0.{b[0]}.{b[1]}.{Math.Max((int)b[2], 1)}");
            if (!await db.Devices.AnyAsync(d => d.IpAddress == candidate, ct) && !db.Devices.Local.Any(d => d.IpAddress == candidate))
            {
                return candidate;
            }
        }
    }

    private static bool NameMatches(string? candidate, string name, string? fqdn)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var c = candidate.Trim();
        return string.Equals(c, name, StringComparison.OrdinalIgnoreCase) ||
               (fqdn is not null && string.Equals(c, fqdn, StringComparison.OrdinalIgnoreCase)) ||
               (c.Contains('.', StringComparison.Ordinal) && string.Equals(c.Split('.')[0], name, StringComparison.OrdinalIgnoreCase) &&
                (fqdn is null || string.Equals(c, fqdn, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? Join(params string?[] parts)
    {
        var s = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return s.Length == 0 ? null : s;
    }

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"^[A-Za-z0-9\-]{8,64}$")]
    private static partial Regex MachineIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_.]{0,62}$")]
    private static partial Regex ComputerNamePattern();
}
