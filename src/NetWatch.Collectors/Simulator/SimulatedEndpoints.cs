using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetWatch.Collectors.Endpoints;

namespace NetWatch.Collectors.Simulator;

/// <summary>
/// --simulate: 20 Windows machines send inventories through the same ingest path as the GPO script, every
/// 5 minutes, with occasional changes (new software, USB stick, extra local admin, firewall off, Defender off).
/// </summary>
public sealed class SimulatedEndpoints : BackgroundService
{
    public const int Count = 20;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly EndpointIngestService _ingest;
    private readonly EndpointStore _store;
    private readonly ILogger<SimulatedEndpoints> _logger;
    private readonly Dictionary<int, string> _tokens = new();
    private readonly Random _random = new(4711);
    private int _round;

    public SimulatedEndpoints(EndpointIngestService ingest, EndpointStore store, ILogger<SimulatedEndpoints> logger)
    {
        _ingest = ingest;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RoundAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task RoundAsync(CancellationToken ct)
    {
        var key = _store.EnrollmentKey();
        for (var i = 1; i <= Count; i++)
        {
            try
            {
                var report = Build(i, _round, _random);
                var machineId = string.Create(CultureInfo.InvariantCulture, $"sim-endpoint-{i:000}-0000");
                _tokens.TryGetValue(i, out var token);
                var result = await _ingest.IngestAsync(new IngestRequest(machineId, token, token is null ? key : null, Ip(i), report, Simulated: true), ct);
                if (result.NewToken is { } t)
                {
                    _tokens[i] = t;
                }
                else if (result.Status == IngestStatus.Unauthorized)
                {
                    // Tokens live only in memory: after a restart the simulated machines enroll again
                    // (what an administrator does with "Reset enrollment" for a reinstalled machine).
                    _tokens.Remove(i);
                    if (_store.FindByMachine(machineId) is { } row)
                    {
                        await _store.ResetTokenAsync(row.Id);
                        result = await _ingest.IngestAsync(new IngestRequest(machineId, null, key, Ip(i), report, Simulated: true), ct);
                        if (result.NewToken is { } again)
                        {
                            _tokens[i] = again;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Simulated endpoint {Index} failed", i);
            }
        }

        _round++;
    }

    private static string Ip(int i) => string.Create(CultureInfo.InvariantCulture, $"10.250.60.{10 + i}");

    /// <summary>A plausible inventory of simulated machine <paramref name="i"/> in round <paramref name="round"/>.</summary>
    internal static EndpointReport Build(int i, int round, Random random)
    {
        var now = DateTime.UtcNow;
        var server = i > 17;
        var name = server ? string.Create(CultureInfo.InvariantCulture, $"SIM-SRV-{i:00}") : string.Create(CultureInfo.InvariantCulture, $"SIM-PC-{i:000}");
        var r = new EndpointReport
        {
            ScriptVersion = "1.0.0",
            CollectedUtc = now,
            DurationSec = 8 + i % 5,
            System = new EpSystem
            {
                ComputerName = name,
                Domain = "corp.sim",
                Fqdn = name.ToLowerInvariant() + ".corp.sim",
                PartOfDomain = true,
                Manufacturer = i % 3 == 0 ? "HP" : "Dell Inc.",
                Model = server ? "PowerEdge R650" : i % 3 == 0 ? "EliteBook 840 G9" : "OptiPlex 7010",
                SerialNumber = string.Create(CultureInfo.InvariantCulture, $"SIM{i:0000}X"),
                BiosVersion = "1.14.2",
                OsName = server ? "Microsoft Windows Server 2022 Standard" : "Microsoft Windows 11 Pro",
                OsVersion = server ? "10.0.20348" : "10.0.22631",
                OsBuild = server ? "20348.2849" : (round > 3 && i % 4 == 0 ? "22631.4602" : "22631.4460"),
                OsDisplayVersion = server ? "21H2" : "23H2",
                OsArchitecture = "64-bit",
                ProductType = server ? 3 : 1,
                InstallDateUtc = now.AddDays(-300 - i),
                LastBootUtc = now.AddHours(-(i * 7 % 90) - 1),
                CpuName = "Intel(R) Core(TM) i7-1365U",
                CpuCores = 10,
                CpuLogical = 12,
                CpuLoadPct = random.Next(2, 60),
                MemoryTotalBytes = 16L << 30,
                MemoryFreeBytes = (long)(random.Next(2, 9)) << 30,
                TimeZone = "(UTC+05:00) Islamabad, Karachi",
                SecureBoot = true,
                TpmPresent = true,
                ConsoleUser = server ? null : string.Create(CultureInfo.InvariantCulture, $"CORP\\user{i:000}"),
            },
        };
        r.Adapters.Add(new EpAdapter { Description = "Intel(R) Ethernet Connection", Mac = string.Create(CultureInfo.InvariantCulture, $"00:1A:2B:60:00:{i:X2}"), Ips = [Ip(i)], Gateways = ["10.250.60.1"], Dns = ["10.250.40.10"], Dhcp = !server });
        var cUsed = Math.Min(97, 40 + i * 3 + round % 3);
        r.Disks.Add(new EpDisk { Drive = "C:", Label = "OS", FileSystem = "NTFS", SizeBytes = 256L << 30, FreeBytes = (256L << 30) * (100 - cUsed) / 100, BitLocker = i % 6 == 0 ? "Off" : "On" });

        string[] procs = ["System", "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "svchost.exe", "explorer.exe", "msedge.exe", "OUTLOOK.EXE", "Teams.exe", "MsMpEng.exe"];
        var pid = 4;
        foreach (var p in procs)
        {
            var user = p is "explorer.exe" or "msedge.exe" or "OUTLOOK.EXE" or "Teams.exe" ? r.System.ConsoleUser ?? "NT AUTHORITY\\SYSTEM" : "NT AUTHORITY\\SYSTEM";
            if (server && user != "NT AUTHORITY\\SYSTEM")
            {
                continue;
            }

            r.Processes.Add(new EpProcess
            {
                Pid = pid,
                ParentPid = 4,
                Name = p,
                Path = p == "System" ? null : $"C:\\Windows\\System32\\{p}",
                CommandLine = p == "svchost.exe" ? "C:\\Windows\\system32\\svchost.exe -k netsvcs -p" : null,
                User = user,
                StartUtc = r.System.LastBootUtc,
                CpuSec = random.Next(1, 500),
                MemoryBytes = random.Next(5, 400) * 1048576L,
                Company = "Microsoft Corporation",
            });
            pid += 112;
        }

        if (i == 7 && round % 4 == 2)
        {
            r.Processes.Add(new EpProcess { Pid = 9001, Name = "anydesk.exe", Path = "C:\\Users\\user007\\Downloads\\AnyDesk.exe", User = "CORP\\user007", Company = "AnyDesk Software GmbH" });
        }

        foreach (var (svc, display, state) in new[] { ("WinDefend", "Microsoft Defender Antivirus Service", "Running"), ("Spooler", "Print Spooler", "Running"), ("wuauserv", "Windows Update", "Stopped"), ("TermService", "Remote Desktop Services", server ? "Running" : "Stopped"), ("W32Time", "Windows Time", "Running") })
        {
            r.Services.Add(new EpService { Name = svc, DisplayName = display, State = state, StartMode = state == "Running" ? "Auto" : "Manual", Account = "LocalSystem", Path = "C:\\Windows\\system32\\svchost.exe" });
        }

        r.Software.Add(new EpSoftware { Name = "Microsoft Edge", Version = round > 2 ? "131.0.2903.70" : "130.0.2849.80", Publisher = "Microsoft Corporation", Scope = "Machine" });
        r.Software.Add(new EpSoftware { Name = "7-Zip 24.08 (x64)", Version = "24.08", Publisher = "Igor Pavlov", Scope = "Machine" });
        r.Software.Add(new EpSoftware { Name = "Microsoft 365 Apps for enterprise - en-us", Version = "16.0.17928.20286", Publisher = "Microsoft Corporation", Scope = "Machine" });
        if (i % 2 == 0)
        {
            r.Software.Add(new EpSoftware { Name = "Google Chrome", Version = "131.0.6778.86", Publisher = "Google LLC", Scope = "Machine" });
        }

        if (i == 7 && round % 4 >= 2)
        {
            r.Software.Add(new EpSoftware { Name = "AnyDesk", Version = "8.1.0", Publisher = "AnyDesk Software GmbH", Scope = "User:CORP\\user007" });
        }

        var lastPatch = now.AddDays(-(i % 5 == 0 ? 70 : 12 + i));
        r.Hotfixes.Add(new EpHotfix { Id = "KB5044284", Description = "Security Update", InstalledUtc = lastPatch });
        r.Hotfixes.Add(new EpHotfix { Id = "KB5043937", Description = "Update", InstalledUtc = lastPatch.AddDays(-30) });
        r.Patches = new EpPatches
        {
            PendingReboot = i % 7 == 0,
            MissingSecurity = i % 5 == 0 ? 3 : 0,
            MissingCritical = 0,
            MissingOther = i % 3,
            LastSearchUtc = now.AddHours(-5),
            LastInstallUtc = lastPatch,
            WsusServer = "http://wsus.corp.sim:8530",
        };
        if (i % 5 == 0)
        {
            r.Patches.Missing.Add(new EpMissingUpdate { Title = "2024-11 Cumulative Update for Windows 11 Version 23H2 (KB5046633)", Kb = "KB5046633", Severity = "Critical", Category = "Security Updates" });
        }

        var publicOff = i == 11 && round % 6 >= 3;
        foreach (var profile in new[] { "Domain", "Private", "Public" })
        {
            r.Firewall.Add(new EpFirewallProfile { Name = profile, Enabled = !(publicOff && profile == "Public"), DefaultInbound = "Block", DefaultOutbound = "Allow" });
        }

        r.Antivirus = new EpAntivirus
        {
            Products = server ? new() : [new EpAvProduct { Name = "Windows Defender", Enabled = true, UpToDate = i != 13 }],
            Defender = new EpDefender
            {
                ServiceEnabled = true,
                RealTimeProtection = i != 13,
                SignatureUtc = now.AddDays(-1),
                SignatureAgeDays = 1,
                SignatureVersion = "1.421.123.0",
                EngineVersion = "1.1.24090.11",
                TamperProtected = true,
                LastQuickScanUtc = now.AddHours(-20),
            },
        };
        if (i == 13)
        {
            r.Antivirus.Products[0].Enabled = false;
        }

        if (!server)
        {
            r.Sessions.Add(new EpSession { User = r.System.ConsoleUser, Session = "console", State = "Active", LogonUtc = now.AddHours(-3) });
        }

        r.LocalAdmins.Add(new EpAccount { Name = name + "\\Administrator", Type = "User", Source = "Local" });
        r.LocalAdmins.Add(new EpAccount { Name = "CORP\\Domain Admins", Type = "Group", Source = "Domain" });
        if (i == 3 && round % 5 >= 2)
        {
            r.LocalAdmins.Add(new EpAccount { Name = "CORP\\user003", Type = "User", Source = "Domain" });
        }

        r.LocalUsers.Add(new EpLocalUser { Name = "Administrator", Enabled = false, PasswordRequired = true });
        r.LocalUsers.Add(new EpLocalUser { Name = "Guest", Enabled = false, PasswordRequired = false });

        r.Listening.Add(new EpPort { Protocol = "TCP", Address = "0.0.0.0", Port = 135, Process = "svchost.exe", Pid = 900 });
        r.Listening.Add(new EpPort { Protocol = "TCP", Address = "0.0.0.0", Port = 445, Process = "System", Pid = 4 });
        if (server)
        {
            r.Listening.Add(new EpPort { Protocol = "TCP", Address = "0.0.0.0", Port = 3389, Process = "svchost.exe", Pid = 1200 });
        }

        r.Connections.Add(new EpConnection { Local = Ip(i), LocalPort = 50000 + i, Remote = "10.250.40.10", RemotePort = 389, State = "Established", Process = "lsass.exe", Pid = 452 });

        r.Usb.Add(new EpUsb { Name = "USB Input Device", DeviceId = "USB\\VID_046D&PID_C077\\5&1", Class = "HIDClass", Present = true });
        if (i == 5 && round % 3 == 1)
        {
            r.Usb.Add(new EpUsb { Name = "SanDisk Cruzer Blade USB Device", DeviceId = string.Create(CultureInfo.InvariantCulture, $"USBSTOR\\DISK&VEN_SANDISK&PROD_CRUZER_BLADE\\4C5300012{round:000}"), Class = "DiskDrive", Storage = true, Present = true, Serial = "4C53000123" });
        }

        r.Startup.Add(new EpStartup { Name = "SecurityHealth", Command = "%windir%\\system32\\SecurityHealthSystray.exe", Location = "HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", User = "Public" });
        r.Shares.Add(new EpShare { Name = "ADMIN$", Path = "C:\\Windows", Description = "Remote Admin" });
        r.Shares.Add(new EpShare { Name = "C$", Path = "C:\\", Description = "Default share" });
        return r;
    }
}
