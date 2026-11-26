using System.Globalization;
using System.Text.RegularExpressions;

namespace NetWatch.Collectors.Endpoints;

/// <summary>Summary values and change detection between two inventories of one machine (no I/O, unit-tested).</summary>
public static partial class EndpointDiff
{
    /// <summary>Changes that also become alert signals (EventKeys.EndpointChange, field "change").</summary>
    public static readonly IReadOnlySet<string> Alertable = new HashSet<string>(StringComparer.Ordinal)
    {
        ChangeKinds.SoftwareInstalled, ChangeKinds.LocalAdminAdded, ChangeKinds.UsbStorage, ChangeKinds.ServiceInstalled,
        ChangeKinds.StartupAdded, ChangeKinds.FirewallDisabled, ChangeKinds.LocalUserCreated, ChangeKinds.PortOpened,
        ChangeKinds.ExecutableAdded, ChangeKinds.HostsModified,
    };

    private const int MaxChanges = 500;

    /// <summary>First port of the Windows dynamic (ephemeral) range.</summary>
    public const int DynamicPortStart = 49152;

    /// <summary>Firewall profiles that are off ("Domain, Public"), "" when all are on, null when unknown.</summary>
    public static string? FirewallOff(EndpointReport r) =>
        r.Firewall.Count == 0 ? null : string.Join(", ", r.Firewall.Where(p => p.Enabled == false).Select(p => p.Name));

    /// <summary>Why the machine is not protected by antivirus, or null when it is (or nothing is known).</summary>
    public static string? AvProblem(EndpointReport r, int maxSignatureAgeDays)
    {
        if (r.Antivirus is not { } av)
        {
            return null;
        }

        var enabledOther = av.Products.Where(p => p.Enabled == true && !IsDefender(p.Name)).ToList();
        if (av.Products.Count > 0 && !av.Products.Any(p => p.Enabled == true))
        {
            return "No antivirus enabled (" + string.Join(", ", av.Products.Select(p => p.Name)) + ")";
        }

        if (enabledOther.Count > 0)
        {
            return enabledOther.All(p => p.UpToDate == false) ? $"Antivirus signatures out of date ({enabledOther[0].Name})" : null;
        }

        if (av.Defender is not { } d)
        {
            return null;
        }

        if (d.ServiceEnabled == false)
        {
            return "Microsoft Defender antivirus service is off";
        }

        if (d.RealTimeProtection == false)
        {
            return "Microsoft Defender real-time protection is off";
        }

        if (d.SignatureAgeDays is { } age && age > maxSignatureAgeDays)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Microsoft Defender signatures are {age} days old");
        }

        return null;
    }

    public static int? MissingUpdates(EndpointReport r) =>
        r.Patches is { } p && (p.MissingSecurity is not null || p.MissingCritical is not null)
            ? (p.MissingSecurity ?? 0) + (p.MissingCritical ?? 0)
            : null;

    public static DateTime? LastPatch(EndpointReport r)
    {
        var fromHotfix = r.Hotfixes.Select(h => h.InstalledUtc).Where(d => d is not null).Max();
        var fromWu = r.Patches?.LastInstallUtc;
        return fromHotfix is null ? fromWu : fromWu is null ? fromHotfix : (fromHotfix > fromWu ? fromHotfix : fromWu);
    }

    public static double? WorstDiskPct(EndpointReport r) =>
        r.Disks.Where(d => d.SizeBytes > 0 && d.FreeBytes is not null)
            .Select(d => Math.Round(100.0 * (d.SizeBytes!.Value - d.FreeBytes!.Value) / d.SizeBytes.Value, 1))
            .DefaultIfEmpty(double.NaN).Max() is var v && double.IsNaN(v) ? null : v;

    public static string? User(EndpointReport r) =>
        !string.IsNullOrWhiteSpace(r.System.ConsoleUser) ? r.System.ConsoleUser
        : r.Sessions.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.User))?.User;

    /// <summary>
    /// The machine's address: the request's source address when it is one of the machine's own addresses (or
    /// nothing better is known), otherwise the first IPv4 address of an adapter with a gateway.
    /// </summary>
    public static string? PrimaryIp(EndpointReport r, string? remoteIp)
    {
        var all = r.Adapters.SelectMany(a => a.Ips.Select(ip => (Ip: ip, HasGateway: a.Gateways.Count > 0)))
            .Where(x => System.Net.IPAddress.TryParse(x.Ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !x.Ip.StartsWith("169.254.", StringComparison.Ordinal) && !x.Ip.StartsWith("127.", StringComparison.Ordinal))
            .ToList();
        if (remoteIp is not null && (all.Count == 0 || all.Any(x => x.Ip == remoteIp)) && !remoteIp.StartsWith("127.", StringComparison.Ordinal) && remoteIp != "::1")
        {
            return remoteIp;
        }

        return all.OrderByDescending(x => x.HasGateway).Select(x => x.Ip).FirstOrDefault() ?? remoteIp;
    }

    /// <summary>Caps list sizes and string lengths so one machine cannot flood the database.</summary>
    public static void Sanitize(EndpointReport r)
    {
        r.Processes = Take(r.Processes, 3000);
        r.Services = Take(r.Services, 2000);
        r.Software = Take(r.Software, 5000);
        r.Hotfixes = Take(r.Hotfixes, 2000);
        r.Connections = Take(r.Connections, 2000);
        r.Listening = Take(r.Listening, 2000);
        r.Usb = Take(r.Usb, 500);
        r.Startup = Take(r.Startup, 500);
        r.Shares = Take(r.Shares, 200);
        r.LocalAdmins = Take(r.LocalAdmins, 500);
        r.LocalUsers = Take(r.LocalUsers, 500);
        r.Sessions = Take(r.Sessions, 200);
        r.Adapters = Take(r.Adapters, 64);
        r.Disks = Take(r.Disks, 64);
        r.Firewall = Take(r.Firewall, 8);
        if (r.Files.Count > 200_000)
        {
            r.Files = r.Files.Take(200_000).ToList();
            r.FilesTruncated = true;
        }

        r.Files.RemoveAll(f => f.Path is null || f.Path.Length > 1024);
        foreach (var f in r.Files.Where(f => f.Sha256 is { } h && (h.Length != 64 || !h.All(Uri.IsHexDigit))))
        {
            f.Sha256 = null;
        }
        if (r.Patches is { } p)
        {
            p.Missing = Take(p.Missing, 500);
        }

        foreach (var x in r.Processes)
        {
            x.CommandLine = Cut(x.CommandLine, 2048);
            x.Path = Cut(x.Path, 1024);
        }

        foreach (var s in r.Services)
        {
            s.Path = Cut(s.Path, 1024);
        }

        foreach (var s in r.Startup)
        {
            s.Command = Cut(s.Command, 1024);
        }

        if (r.Errors.Count > 50)
        {
            r.Errors = r.Errors.Take(50).ToDictionary(kv => kv.Key, kv => Cut(kv.Value, 500)!, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Everything that changed from <paramref name="previous"/> to <paramref name="current"/>; empty for the first report.</summary>
    public static List<(string Kind, string Item, string? Detail)> Compare(EndpointReport? previous, EndpointReport current)
    {
        var changes = new List<(string, string, string?)>();
        if (previous is null)
        {
            return changes;
        }

        // Only compare sections both reports have (a section that failed this time is not "everything removed").
        if (!Failed(previous, "software") && !Failed(current, "software"))
        {
            var before = ByKey(previous.Software, s => s.Name);
            var after = ByKey(current.Software, s => s.Name);
            foreach (var (name, s) in after)
            {
                if (!before.TryGetValue(name, out var old))
                {
                    changes.Add((ChangeKinds.SoftwareInstalled, s.Name!, Join(s.Version, s.Publisher)));
                }
                else if (!string.Equals(old.Version, s.Version, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add((ChangeKinds.SoftwareUpdated, s.Name!, $"{old.Version ?? "?"} → {s.Version ?? "?"}"));
                }
            }

            changes.AddRange(before.Where(kv => !after.ContainsKey(kv.Key)).Select(kv => (ChangeKinds.SoftwareRemoved, kv.Value.Name!, Join(kv.Value.Version, kv.Value.Publisher))));
        }

        if (!Failed(previous, "localAdmins") && !Failed(current, "localAdmins"))
        {
            Diff(previous.LocalAdmins, current.LocalAdmins, a => a.Name, ChangeKinds.LocalAdminAdded, ChangeKinds.LocalAdminRemoved, a => Join(a.Type, a.Source), changes);
        }

        if (!Failed(previous, "localUsers") && !Failed(current, "localUsers"))
        {
            Diff(previous.LocalUsers, current.LocalUsers, u => u.Name, ChangeKinds.LocalUserCreated, ChangeKinds.LocalUserDeleted, _ => null, changes);
        }

        if (!Failed(previous, "services") && !Failed(current, "services"))
        {
            Diff(previous.Services, current.Services, s => s.Name, ChangeKinds.ServiceInstalled, ChangeKinds.ServiceRemoved, s => Join(s.DisplayName, s.Path), changes);
        }

        if (!Failed(previous, "startup") && !Failed(current, "startup"))
        {
            Diff(previous.Startup, current.Startup, s => s.Name + " | " + s.Location, ChangeKinds.StartupAdded, ChangeKinds.StartupRemoved, s => s.Command, changes, s => s.Name);
        }

        if (!Failed(previous, "listening") && !Failed(current, "listening"))
        {
            // Ports in the dynamic range (49152+) come and go with every browser or RPC client: not a change.
            Diff(previous.Listening.Where(p => p.Port < DynamicPortStart), current.Listening.Where(p => p.Port < DynamicPortStart),
                p => $"{p.Protocol} {p.Port.ToString(CultureInfo.InvariantCulture)}", ChangeKinds.PortOpened, ChangeKinds.PortClosed, p => p.Process, changes);
        }

        if (!Failed(previous, "usb") && !Failed(current, "usb"))
        {
            var seen = previous.Usb.Where(u => u.DeviceId is not null).Select(u => u.DeviceId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var u in current.Usb.Where(u => u.DeviceId is not null && !seen.Contains(u.DeviceId)))
            {
                if (u.Storage)
                {
                    changes.Add((ChangeKinds.UsbStorage, u.Name ?? u.DeviceId!, u.Present ? Join(u.Serial, u.DeviceId) : "plugged in and removed again since the last inventory · " + u.DeviceId));
                }
                else if (u.Present && !UsbInfrastructure().IsMatch(u.Name ?? string.Empty))
                {
                    changes.Add((ChangeKinds.UsbDevice, u.Name ?? u.DeviceId!, Join(u.Class, u.DeviceId)));
                }
            }
        }

        if (current.Firewall.Count > 0 && previous.Firewall.Count > 0)
        {
            foreach (var p in current.Firewall)
            {
                var old = previous.Firewall.FirstOrDefault(o => string.Equals(o.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (old?.Enabled == true && p.Enabled == false)
                {
                    changes.Add((ChangeKinds.FirewallDisabled, $"{p.Name} profile", null));
                }
                else if (old?.Enabled == false && p.Enabled == true)
                {
                    changes.Add((ChangeKinds.FirewallEnabled, $"{p.Name} profile", null));
                }
            }
        }

        if (!Failed(previous, "hotfixes") && !Failed(current, "hotfixes"))
        {
            var old = previous.Hotfixes.Where(h => h.Id is not null).Select(h => h.Id!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            changes.AddRange(current.Hotfixes.Where(h => h.Id is not null && !old.Contains(h.Id)).Select(h => (ChangeKinds.UpdateInstalled, h.Id!, h.Description)));
        }

        if (previous.System.OsBuild is { } b1 && current.System.OsBuild is { } b2 && b1 != b2)
        {
            changes.Add((ChangeKinds.OsUpdated, current.System.OsName ?? "Windows", $"build {b1} → {b2}"));
        }

        return changes.Count > MaxChanges ? changes.Take(MaxChanges).ToList() : changes;
    }

    /// <summary>Extensions of files that run code (programs, scripts, installers, disk images used to deliver malware).</summary>
    public static readonly IReadOnlySet<string> ExecutableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "msi", "msix", "appx", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta", "scr", "com", "pif",
        "dll", "cpl", "jar", "iso", "img", "vhd", "vhdx", "reg",
    };

    private const int MaxFileChangesPerKind = 200;

    /// <summary>
    /// Compares a machine's new file list with the stored one. Returns the rows to write and the changes to record:
    /// new files (executables separately), deleted and modified files (size or date changed, or a different hash);
    /// a file that only got its hash now is updated silently. <paramref name="baseline"/> (first list from this
    /// machine) records no changes. Each kind is capped, with one summary row for the rest.
    /// </summary>
    public static (FileDelta Delta, List<(string Kind, string Item, string? Detail)> Changes) Files(
        IReadOnlyDictionary<string, EndpointFileRow> stored, IReadOnlyList<EpFile> current, bool baseline)
    {
        var upserts = new List<EpFile>();
        var created = new List<EpFile>();
        var executables = new List<EpFile>();
        var modified = new List<(EpFile File, EndpointFileRow Old)>();
        var hostsChanged = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in current)
        {
            if (string.IsNullOrWhiteSpace(f.Path) || !seen.Add(f.Path))
            {
                continue;
            }

            if (!stored.TryGetValue(f.Path, out var old))
            {
                upserts.Add(f);
                (ExecutableExtensions.Contains(EndpointStore.Extension(f.Path)) ? executables : created).Add(f);
                continue;
            }

            var sameDate = old.ModifiedUtc is { } a && f.ModifiedUtc is { } b ? Math.Abs((a - b).TotalSeconds) < 2 : old.ModifiedUtc == f.ModifiedUtc;
            var hashDiffers = old.Sha256 is not null && f.Sha256 is not null && !string.Equals(old.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase);
            if (old.Size != f.Size || !sameDate || hashDiffers)
            {
                upserts.Add(f);
                modified.Add((f, old));
                hostsChanged |= f.Folder == "System" && f.Path.EndsWith(@"\etc\hosts", StringComparison.OrdinalIgnoreCase);
            }
            else if (old.Sha256 is null && f.Sha256 is not null)
            {
                upserts.Add(f);
            }
        }

        var removed = stored.Keys.Where(p => !seen.Contains(p)).ToList();
        var delta = new FileDelta(upserts, removed, seen.Count);
        var changes = new List<(string, string, string?)>();
        if (baseline)
        {
            return (delta, changes);
        }

        AddCapped(changes, ChangeKinds.ExecutableAdded, executables.Select(f => (f.Path!, FileDetail(f))));
        AddCapped(changes, ChangeKinds.FileCreated, created.Select(f => (f.Path!, FileDetail(f))));
        AddCapped(changes, ChangeKinds.FileModified, modified.Select(m => (m.File.Path!, $"{Size(m.Old.Size)} → {Size(m.File.Size)}" + (m.File.Sha256 is { } h ? $" · SHA-256 {h[..12]}…" : string.Empty))));
        AddCapped(changes, ChangeKinds.FileDeleted, removed.Select(p => (p, FileDetail(stored[p]))));
        if (hostsChanged)
        {
            changes.Add((ChangeKinds.HostsModified, current.First(f => f.Folder == "System" && f.Path!.EndsWith(@"\etc\hosts", StringComparison.OrdinalIgnoreCase)).Path!, "the hosts file redirects names before DNS"));
        }

        return (delta, changes);
    }

    private static void AddCapped(List<(string, string, string?)> changes, string kind, IEnumerable<(string Item, string Detail)> items)
    {
        var list = items.ToList();
        changes.AddRange(list.Take(MaxFileChangesPerKind).Select(x => (kind, x.Item, (string?)x.Detail)));
        if (list.Count > MaxFileChangesPerKind)
        {
            changes.Add((kind, $"… and {list.Count - MaxFileChangesPerKind} more", "not listed one by one"));
        }
    }

    private static string FileDetail(EpFile f) => $"{Size(f.Size)} · {f.User} / {f.Folder}" + (f.Sha256 is { } h ? $" · SHA-256 {h}" : string.Empty);

    private static string FileDetail(EndpointFileRow f) => $"{Size(f.Size)} · {f.User} / {f.Folder}";

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    /// <summary>Alert message for an alertable change.</summary>
    public static string Message(string machine, string kind, string item, string? detail) => kind switch
    {
        ChangeKinds.SoftwareInstalled => $"New software installed on {machine}: {item}{Suffix(detail)}",
        ChangeKinds.LocalAdminAdded => $"New local administrator on {machine}: {item}{Suffix(detail)}",
        ChangeKinds.UsbStorage => $"USB storage device connected to {machine}: {item}{Suffix(detail)}",
        ChangeKinds.ServiceInstalled => $"New service on {machine}: {item}{Suffix(detail)}",
        ChangeKinds.StartupAdded => $"New startup program on {machine}: {item}{Suffix(detail)}",
        ChangeKinds.FirewallDisabled => $"Windows Firewall turned off on {machine}: {item}",
        ChangeKinds.LocalUserCreated => $"New local user account on {machine}: {item}",
        ChangeKinds.PortOpened => $"New listening port on {machine}: {item}{Suffix(detail)}",
        ChangeKinds.ExecutableAdded => $"New program or script file on {machine}: {item}{Suffix(detail)}",
        ChangeKinds.HostsModified => $"Hosts file changed on {machine}: {item}",
        _ => $"{kind} on {machine}: {item}{Suffix(detail)}",
    };

    private static string Suffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";

    private static bool Failed(EndpointReport r, string section) => r.Errors.ContainsKey(section);

    private static bool IsDefender(string? name) => name is not null && name.Contains("Defender", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, T> ByKey<T>(IEnumerable<T> items, Func<T, string?> key)
    {
        var d = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in items)
        {
            if (key(x) is { Length: > 0 } k)
            {
                d.TryAdd(k.Trim(), x);
            }
        }

        return d;
    }

    private static void Diff<T>(IEnumerable<T> before, IEnumerable<T> after, Func<T, string?> key, string added, string removed, Func<T, string?> detail,
        List<(string, string, string?)> changes, Func<T, string?>? label = null)
    {
        var b = ByKey(before, key);
        var a = ByKey(after, key);
        label ??= key;
        changes.AddRange(a.Where(kv => !b.ContainsKey(kv.Key)).Select(kv => (added, label(kv.Value) ?? kv.Key, detail(kv.Value))));
        changes.AddRange(b.Where(kv => !a.ContainsKey(kv.Key)).Select(kv => (removed, label(kv.Value) ?? kv.Key, detail(kv.Value))));
    }

    private static string? Join(params string?[] parts)
    {
        var s = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return s.Length == 0 ? null : s;
    }

    private static List<T> Take<T>(List<T>? list, int max) => list is null ? new List<T>() : list.Count <= max ? list : list.Take(max).ToList();

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"\bhub\b|host controller|composite device|root|usb input device|hid-compliant", RegexOptions.IgnoreCase)]
    private static partial Regex UsbInfrastructure();
}

/// <summary>Kinds of endpoint changes (stored in endpoints.db and sent as the "change" field of alert signals).</summary>
public static class ChangeKinds
{
    public const string Enrolled = "enrolled";
    public const string SoftwareInstalled = "softwareInstalled";
    public const string SoftwareRemoved = "softwareRemoved";
    public const string SoftwareUpdated = "softwareUpdated";
    public const string LocalAdminAdded = "localAdminAdded";
    public const string LocalAdminRemoved = "localAdminRemoved";
    public const string LocalUserCreated = "localUserCreated";
    public const string LocalUserDeleted = "localUserDeleted";
    public const string ServiceInstalled = "serviceInstalled";
    public const string ServiceRemoved = "serviceRemoved";
    public const string StartupAdded = "startupAdded";
    public const string StartupRemoved = "startupRemoved";
    public const string PortOpened = "portOpened";
    public const string PortClosed = "portClosed";
    public const string UsbStorage = "usbStorage";
    public const string UsbDevice = "usbDevice";
    public const string FirewallDisabled = "firewallDisabled";
    public const string FirewallEnabled = "firewallEnabled";
    public const string UpdateInstalled = "updateInstalled";
    public const string OsUpdated = "osUpdated";
    public const string FileCreated = "fileCreated";
    public const string ExecutableAdded = "executableAdded";
    public const string FileModified = "fileModified";
    public const string FileDeleted = "fileDeleted";
    public const string HostsModified = "hostsModified";
}
