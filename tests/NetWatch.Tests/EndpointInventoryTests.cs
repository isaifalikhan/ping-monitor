using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Collectors;
using NetWatch.Collectors.Endpoints;
using NetWatch.Collectors.Inventory;
using NetWatch.Collectors.Simulator;
using NetWatch.Core;
using NetWatch.Core.Alerts;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Core.Monitoring;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Api;

namespace NetWatch.Tests;

/// <summary>
/// Agentless endpoint inventory: the GPO scheduled-task script's report (enrollment, tokens, device linking, change
/// detection, alert signals), the endpoint events forwarded with WEF, and the deployment kit. The script itself and
/// real GPO / WEF delivery are covered on a real machine (PENDING PILOT for the domain rollout).
/// </summary>
public sealed class EndpointInventoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-endpoints-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc));
    private readonly RecordingSink _signals = new();
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetWatchData(new NetWatchPaths(_root));
        services.AddNetWatchCollectors();
        services.AddSingleton<TimeProvider>(_clock); // after AddNetWatchData, which registers TimeProvider.System
        services.AddSingleton<IAlertSignalSink>(_signals);
        _sp = services.BuildServiceProvider();
        await _sp.InitialiseDatabasesAsync(NullLogger.Instance);
        await Inventory.ReloadAsync(CancellationToken.None);
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

    private EndpointIngestService Ingest => _sp.GetRequiredService<EndpointIngestService>();

    private EndpointStore Store => _sp.GetRequiredService<EndpointStore>();

    private InventoryCache Inventory => _sp.GetRequiredService<InventoryCache>();

    private static EndpointReport Report(int i = 1, int round = 0) => SimulatedEndpoints.Build(i, round, new Random(1));

    private Task<IngestResult> Send(EndpointReport report, string machine = "test-machine-0001", string? token = null, string? key = null, string ip = "10.9.0.11") =>
        Ingest.IngestAsync(new IngestRequest(machine, token, key ?? (token is null ? Store.EnrollmentKey() : null), ip, report), CancellationToken.None);

    // ---------------------------------------------------------------- enrollment and tokens

    [Fact]
    public async Task Enrollment_needs_the_key_then_only_the_machine_token_works()
    {
        var wrongKey = await Send(Report(), key: "not-the-key");
        Assert.Equal(IngestStatus.Unauthorized, wrongKey.Status);
        Assert.Null(Store.FindByMachine("test-machine-0001"));

        var enrolled = await Send(Report());
        Assert.Equal(IngestStatus.Ok, enrolled.Status);
        Assert.False(string.IsNullOrEmpty(enrolled.NewToken));

        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(IngestStatus.Unauthorized, (await Send(Report(), token: "forged-token")).Status);

        // With the key but without the token, an enrolled machine ID cannot be taken over.
        Assert.Equal(IngestStatus.Unauthorized, (await Send(Report())).Status);

        var next = await Send(Report(), token: enrolled.NewToken);
        Assert.Equal(IngestStatus.Ok, next.Status);
        Assert.Null(next.NewToken);

        // Reports faster than once a minute are refused.
        Assert.Equal(IngestStatus.TooSoon, (await Send(Report(), token: enrolled.NewToken)).Status);

        // The token is stored only as a hash.
        Assert.NotEqual(enrolled.NewToken, Store.FindByMachine("test-machine-0001")!.TokenHash);
    }

    [Fact]
    public async Task Rotated_key_keeps_enrolled_machines_working_and_reset_requires_the_new_key()
    {
        var enrolled = await Send(Report());
        var oldKey = Store.EnrollmentKey();
        var newKey = Store.RotateEnrollmentKey();
        Assert.NotEqual(oldKey, newKey);

        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(IngestStatus.Ok, (await Send(Report(), token: enrolled.NewToken)).Status);

        await Store.ResetTokenAsync(Store.FindByMachine("test-machine-0001")!.Id);
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(IngestStatus.Unauthorized, (await Send(Report(), key: oldKey)).Status);
        var again = await Send(Report(), key: newKey);
        Assert.Equal(IngestStatus.Ok, again.Status);
        Assert.NotNull(again.NewToken);
    }

    [Fact]
    public async Task A_second_machine_with_an_active_name_is_refused_but_a_silent_one_is_replaced()
    {
        Assert.Equal(IngestStatus.Ok, (await Send(Report(), machine: "machine-aaaa-0001")).Status);
        _clock.Advance(TimeSpan.FromHours(2));
        var clash = await Send(Report(), machine: "machine-bbbb-0002");
        Assert.Equal(IngestStatus.Conflict, clash.Status);

        _clock.Advance(TimeSpan.FromHours(30));
        Assert.Equal(IngestStatus.Ok, (await Send(Report(), machine: "machine-bbbb-0002")).Status);
        Assert.Null(Store.FindByMachine("machine-aaaa-0001"));
    }

    [Theory]
    [InlineData("short", "PC01")]
    [InlineData("bad machine id!", "PC01")]
    [InlineData("good-machine-0001", "bad name")]
    [InlineData("good-machine-0001", "")]
    public async Task Invalid_identities_are_refused(string machine, string name)
    {
        var r = Report();
        r.System.ComputerName = name;
        Assert.Equal(IngestStatus.Invalid, (await Send(r, machine: machine)).Status);
    }

    [Fact]
    public async Task Turned_off_inventory_refuses_reports()
    {
        var settings = _sp.GetRequiredService<SettingsService>();
        await settings.SaveAsync(EndpointSettings.Key, new EndpointSettings { Enabled = false }, null, CancellationToken.None);
        Assert.Equal(IngestStatus.Disabled, (await Send(Report())).Status);
    }

    // ---------------------------------------------------------------- devices

    [Fact]
    public async Task Unknown_workstation_becomes_a_Workstation_device_that_is_not_polled()
    {
        var result = await Send(Report(3));
        var device = Inventory.GetSnapshot(result.DeviceId!.Value)!;
        Assert.Equal(DeviceType.Workstation, device.Type);
        Assert.Equal("SIM-PC-003", device.Name);
        Assert.Equal("10.250.60.13", device.IpAddress);
        Assert.Contains(EndpointIngestService.EndpointTag, device.Tags);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        var row = await db.Devices.SingleAsync(d => d.Id == device.Id);
        Assert.Equal(DeviceStatus.Up, row.Status);
        Assert.Equal(OsKind.Windows, row.Os);
        Assert.Equal("SIM0003X", row.SerialNumber);
    }

    [Fact]
    public async Task Existing_server_is_matched_by_name_or_fixed_ip_and_never_renumbered()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            db.Devices.Add(new Device { Name = "File server", IpAddress = "10.250.60.28", Type = DeviceType.Server, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
            db.Devices.Add(new Device { Name = "sim-pc-004.corp.sim", IpAddress = "10.1.1.4", Type = DeviceType.Other, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await Inventory.ReloadAsync(CancellationToken.None);
        var server = await Send(Report(18), machine: "server-018-0000", ip: "10.250.60.28");
        Assert.Equal("File server", Inventory.GetSnapshot(server.DeviceId!.Value)!.Name);

        var byName = await Send(Report(4), machine: "pc-0004-0000", ip: "10.250.60.14");
        var snap = Inventory.GetSnapshot(byName.DeviceId!.Value)!;
        Assert.Equal("sim-pc-004.corp.sim", snap.Name);
        Assert.Equal("10.1.1.4", snap.IpAddress);
    }

    [Fact]
    public async Task Dhcp_address_moving_to_another_workstation_moves_the_old_holder_to_a_placeholder()
    {
        var a = Report(1);
        a.Adapters[0].Ips = ["10.9.9.9"];
        var first = await Send(a, machine: "machine-one-0001", ip: "10.9.9.9");

        var b = Report(2);
        b.Adapters[0].Ips = ["10.9.9.9"];
        var second = await Send(b, machine: "machine-two-0002", ip: "10.9.9.9");

        await Inventory.ReloadAsync(CancellationToken.None);
        Assert.Equal("10.9.9.9", Inventory.GetSnapshot(second.DeviceId!.Value)!.IpAddress);
        Assert.StartsWith("0.", Inventory.GetSnapshot(first.DeviceId!.Value)!.IpAddress, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- changes and signals

    [Fact]
    public async Task Changes_between_two_inventories_are_stored_and_raise_signals()
    {
        var first = await Send(Report(7, 0), machine: "machine-007-0000");
        Assert.Single(Store.Changes(first.EndpointId, DateTime.MinValue, null, 100), c => c.Kind == ChangeKinds.Enrolled);
        Assert.DoesNotContain(_signals.All.OfType<EventSignal>(), s => s.EventKey == EventKeys.EndpointChange);

        var next = Report(7, 2); // round 2: AnyDesk installed and running
        next.LocalAdmins.Add(new EpAccount { Name = "CORP\\intruder", Type = "User", Source = "Domain" });
        next.Usb.Add(new EpUsb { Name = "Kingston DataTraveler", DeviceId = "USBSTOR\\DISK&VEN_KINGSTON\\123", Storage = true, Present = false });
        next.Firewall.Single(f => f.Name == "Public").Enabled = false;
        next.Listening.Add(new EpPort { Protocol = "TCP", Address = "0.0.0.0", Port = 5900, Process = "vnc.exe" });
        next.Listening.Add(new EpPort { Protocol = "UDP", Address = "0.0.0.0", Port = 61000, Process = "chrome.exe" });
        _clock.Advance(TimeSpan.FromMinutes(30));
        var result = await Send(next, machine: "machine-007-0000", token: first.NewToken);
        Assert.Equal(IngestStatus.Ok, result.Status);

        var kinds = Store.Changes(first.EndpointId, DateTime.MinValue, null, 100).Select(c => (c.Kind, c.Item)).ToList();
        Assert.Contains((ChangeKinds.SoftwareInstalled, "AnyDesk"), kinds);
        Assert.Contains((ChangeKinds.LocalAdminAdded, "CORP\\intruder"), kinds);
        Assert.Contains((ChangeKinds.UsbStorage, "Kingston DataTraveler"), kinds);
        Assert.Contains((ChangeKinds.FirewallDisabled, "Public profile"), kinds);
        Assert.Contains((ChangeKinds.PortOpened, "TCP 5900"), kinds);
        Assert.DoesNotContain(kinds, k => k.Item == "UDP 61000");

        var events = _signals.All.OfType<EventSignal>().Where(s => s.EventKey == EventKeys.EndpointChange).ToList();
        Assert.Contains(events, e => e.Fields!["change"] == ChangeKinds.LocalAdminAdded && e.Severity == Severity.Major);
        Assert.Contains(events, e => e.Fields!["change"] == ChangeKinds.SoftwareInstalled && e.Message.Contains("AnyDesk", StringComparison.Ordinal));
        Assert.Contains(_signals.All.OfType<StateSignal>(), s => s.StateKey == StateKeys.EndpointFirewallOff && s.Instance == "Public" && s.IsProblem);

        // The software and process tables answer fleet-wide searches.
        Assert.Contains(Store.SoftwareSummary("anydesk", 10), s => s.Name == "AnyDesk" && s.Machines == 1);
        Assert.Contains(Store.SearchProcesses("anydesk", 10), p => p.Name == "anydesk.exe");
    }

    [Fact]
    public async Task A_section_that_failed_on_the_machine_is_not_reported_as_everything_removed()
    {
        var first = await Send(Report(2), machine: "machine-002-0000");
        var broken = Report(2);
        broken.Software.Clear();
        broken.Errors["software"] = "Access denied";
        _clock.Advance(TimeSpan.FromMinutes(30));
        await Send(broken, machine: "machine-002-0000", token: first.NewToken);
        Assert.DoesNotContain(Store.Changes(first.EndpointId, DateTime.MinValue, null, 100), c => c.Kind == ChangeKinds.SoftwareRemoved);
        Assert.Contains("software: Access denied", Store.Get(first.EndpointId!.Value)!.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Antivirus_patch_and_disk_signals_are_published()
    {
        var result = await Send(Report(13), machine: "machine-013-0000"); // Defender disabled in the simulator
        var id = result.DeviceId!.Value;
        Assert.Contains(_signals.All.OfType<StateSignal>(), s => s.DeviceId == id && s.StateKey == StateKeys.EndpointAntivirus && s.IsProblem);
        Assert.Contains(_signals.All.OfType<MetricSignal>(), s => s.DeviceId == id && s.MetricKey == MetricKeys.EndpointPatchAgeDays);
        Assert.Contains(_signals.All.OfType<MetricSignal>(), s => s.DeviceId == id && s.MetricKey == MetricKeys.DiskUsed && s.Instance == "C:");
        Assert.Contains(_signals.All.OfType<StateSignal>(), s => s.DeviceId == id && s.StateKey == StateKeys.EndpointStale && !s.IsProblem);
    }

    [Fact]
    public async Task Silent_machine_raises_stale_and_its_workstation_turns_unknown()
    {
        var result = await Send(Report(9), machine: "machine-009-0000");
        _clock.Advance(TimeSpan.FromHours(49));
        var monitor = _sp.GetRequiredService<EndpointMonitor>();
        Assert.Equal(1, await monitor.CheckAsync(_clock.GetUtcNow().UtcDateTime, CancellationToken.None));
        Assert.Contains(_signals.All.OfType<StateSignal>(), s => s.DeviceId == result.DeviceId && s.StateKey == StateKeys.EndpointStale && s.IsProblem);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
        Assert.Equal(DeviceStatus.Unknown, (await db.Devices.SingleAsync(d => d.Id == result.DeviceId)).Status);
    }

    [Fact]
    public void Antivirus_rules()
    {
        EndpointReport With(EpAntivirus av) => new() { Antivirus = av };
        var freshDefender = new EpDefender { ServiceEnabled = true, RealTimeProtection = true, SignatureAgeDays = 1 };
        Assert.Null(EndpointDiff.AvProblem(With(new EpAntivirus { Products = [new EpAvProduct { Name = "Windows Defender", Enabled = true }], Defender = freshDefender }), 7));
        Assert.NotNull(EndpointDiff.AvProblem(With(new EpAntivirus { Products = [new EpAvProduct { Name = "Windows Defender", Enabled = false }] }), 7));

        // A third-party product protecting: Defender being passive is normal.
        Assert.Null(EndpointDiff.AvProblem(With(new EpAntivirus
        {
            Products = [new EpAvProduct { Name = "Windows Defender", Enabled = false }, new EpAvProduct { Name = "ESET Endpoint Security", Enabled = true, UpToDate = true }],
            Defender = new EpDefender { RealTimeProtection = false },
        }), 7));

        // Servers have no Security Center: Defender itself decides.
        Assert.Contains("real-time", EndpointDiff.AvProblem(With(new EpAntivirus { Defender = new EpDefender { ServiceEnabled = true, RealTimeProtection = false } }), 7)!, StringComparison.Ordinal);
        Assert.Contains("9 days", EndpointDiff.AvProblem(With(new EpAntivirus { Defender = new EpDefender { ServiceEnabled = true, RealTimeProtection = true, SignatureAgeDays = 9 } }), 7)!, StringComparison.Ordinal);
        Assert.Null(EndpointDiff.AvProblem(With(new EpAntivirus { Defender = new EpDefender { ServiceEnabled = true, RealTimeProtection = true, SignatureAgeDays = 9 } }), 10));
        Assert.Null(EndpointDiff.AvProblem(new EndpointReport(), 7));
    }

    [Fact]
    public void Primary_ip_prefers_the_source_address_when_it_is_the_machines_own()
    {
        var r = new EndpointReport
        {
            Adapters =
            [
                new EpAdapter { Ips = ["169.254.1.1", "fe80::1"] },
                new EpAdapter { Ips = ["192.168.56.1"] },
                new EpAdapter { Ips = ["10.1.2.3"], Gateways = ["10.1.2.1"] },
            ],
        };
        Assert.Equal("192.168.56.1", EndpointDiff.PrimaryIp(r, "192.168.56.1"));
        Assert.Equal("10.1.2.3", EndpointDiff.PrimaryIp(r, "172.20.0.5")); // behind NAT / proxy
        Assert.Equal("10.1.2.3", EndpointDiff.PrimaryIp(r, "127.0.0.1"));
        Assert.Equal("172.20.0.5", EndpointDiff.PrimaryIp(new EndpointReport(), "172.20.0.5"));
    }

    [Fact]
    public void Oversized_reports_are_capped()
    {
        var r = new EndpointReport { Processes = Enumerable.Range(0, 5000).Select(i => new EpProcess { Pid = i, Name = "x", CommandLine = new string('a', 10000) }).ToList() };
        EndpointDiff.Sanitize(r);
        Assert.Equal(3000, r.Processes.Count);
        Assert.Equal(2048, r.Processes[0].CommandLine!.Length);
    }

    // ---------------------------------------------------------------- files (Desktop, Downloads, Documents)

    private static EpFile F(string path, long size = 100, string? sha = null, int dayOffset = 0, string folder = "Downloads") => new()
    {
        Path = path, User = "u1", Folder = folder, Size = size, ModifiedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(dayOffset), Sha256 = sha,
    };

    private static EndpointFileRow Row(EpFile f) => new(1, f.Path!, f.User, f.Folder, f.Size, f.ModifiedUtc, f.CreatedUtc, f.Sha256);

    [Fact]
    public void File_diff_finds_new_programs_modified_deleted_and_hosts_changes()
    {
        var hostsOld = F(@"C:\Windows\System32\drivers\etc\hosts", 824, new string('1', 64), folder: "System");
        var stored = new[] { F(@"C:\Users\u1\Documents\a.docx", 10), F(@"C:\Users\u1\Documents\old.txt", 5), F(@"C:\Users\u1\Desktop\big.iso", 9, sha: null), hostsOld }
            .ToDictionary(f => f.Path!, Row, StringComparer.OrdinalIgnoreCase);
        var current = new List<EpFile>
        {
            F(@"C:\Users\u1\Documents\a.docx", 99, dayOffset: 1),                      // modified
            F(@"C:\Users\u1\Desktop\big.iso", 9, sha: new string('F', 64)),            // only got its hash: silent update
            F(@"C:\Users\u1\Downloads\setup.EXE", 1000),                              // program
            F(@"C:\Users\u1\Downloads\photo.jpg", 2000),                              // ordinary new file
            F(@"C:\Windows\System32\drivers\etc\hosts", 900, new string('2', 64), dayOffset: 2, folder: "System"),
        };

        var (delta, changes) = EndpointDiff.Files(stored, current, baseline: false);
        Assert.Contains((ChangeKinds.ExecutableAdded, @"C:\Users\u1\Downloads\setup.EXE"), changes.Select(c => (c.Kind, c.Item)));
        Assert.Contains((ChangeKinds.FileCreated, @"C:\Users\u1\Downloads\photo.jpg"), changes.Select(c => (c.Kind, c.Item)));
        Assert.Contains((ChangeKinds.FileModified, @"C:\Users\u1\Documents\a.docx"), changes.Select(c => (c.Kind, c.Item)));
        Assert.Contains((ChangeKinds.FileDeleted, @"C:\Users\u1\Documents\old.txt"), changes.Select(c => (c.Kind, c.Item)));
        Assert.Contains(changes, c => c.Kind == ChangeKinds.HostsModified);
        Assert.DoesNotContain(changes, c => c.Item.EndsWith("big.iso", StringComparison.Ordinal));
        Assert.Contains(delta.Upserts, f => f.Path!.EndsWith("big.iso", StringComparison.Ordinal));
        Assert.Equal([@"C:\Users\u1\Documents\old.txt"], delta.Removed);
        Assert.Equal(5, delta.Total);

        // The first list from a machine is the baseline: rows are written, nothing is reported as new.
        var (first, none) = EndpointDiff.Files(new Dictionary<string, EndpointFileRow>(), current, baseline: true);
        Assert.Empty(none);
        Assert.Equal(5, first.Upserts.Count);
    }

    [Fact]
    public void File_changes_are_capped_with_a_summary_row()
    {
        var many = Enumerable.Range(0, 450).Select(i => F($@"C:\Users\u1\Documents\f{i}.txt")).ToList();
        var (_, changes) = EndpointDiff.Files(new Dictionary<string, EndpointFileRow>(), many, baseline: false);
        Assert.Equal(201, changes.Count);
        Assert.Contains(changes, c => c.Item == "… and 250 more");
    }

    [Fact]
    public async Task File_lists_are_stored_searched_and_compared_between_reports()
    {
        var first = Report(9, 0);
        var enrolled = await Send(first, machine: "machine-009-0000");
        var row = Store.FindByMachine("machine-009-0000")!;
        Assert.Equal(first.Files.Count, row.FileCount);
        Assert.DoesNotContain(Store.Changes(row.Id, DateTime.MinValue, null, 100), c => c.Kind is ChangeKinds.FileCreated or ChangeKinds.ExecutableAdded);

        // The file list is kept in the files table, not in the compressed report.
        Assert.Empty(Store.Report(row.Id)!.Files);
        Assert.Contains(Store.Files(row.Id, "budget", null, 10), f => f.Path.EndsWith("Budget 2026.xlsx", StringComparison.Ordinal));

        // Round 1: a ".pdf.js" lands on the Desktop (simulator machine 9).
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(IngestStatus.Ok, (await Send(Report(9, 1), machine: "machine-009-0000", token: enrolled.NewToken)).Status);
        Assert.Contains(Store.Changes(row.Id, DateTime.MinValue, ChangeKinds.ExecutableAdded, 10), c => c.Item.EndsWith("invoice_8841.pdf.js", StringComparison.Ordinal));
        Assert.Contains(_signals.All.OfType<EventSignal>(), s => s.EventKey == EventKeys.EndpointChange && s.Fields!["change"] == ChangeKinds.ExecutableAdded);

        // Fleet-wide search by extension and by hash.
        Assert.Contains(Store.SearchFiles(null, ".JS", null, null, null, 10), f => f.Path.EndsWith(".pdf.js", StringComparison.Ordinal));
        var hash = "9F8E7D6C5B4A3928170605F4E3D2C1B0A9F8E7D6C5B4A3928170605F4E3D2C1B";
        Assert.Single(Store.SearchFiles(null, null, hash.ToLowerInvariant(), null, null, 10));
        Assert.Contains(Store.SearchFiles(null, null, null, 1, "Desktop", 10), f => f.Size >= 1024 * 1024);

        // A report from a machine whose file collection failed leaves the stored list alone.
        var broken = Report(9, 1);
        broken.FilesCollected = false;
        broken.Files.Clear();
        _clock.Advance(TimeSpan.FromMinutes(30));
        await Send(broken, machine: "machine-009-0000", token: enrolled.NewToken);
        Assert.DoesNotContain(Store.Changes(row.Id, DateTime.MinValue, ChangeKinds.FileDeleted, 10), _ => true);
        Assert.NotEmpty(Store.Files(row.Id, null, null, 10));

        // Removing the machine removes its files.
        await Store.DeleteAsync(row.Id);
        Assert.Empty(Store.Files(row.Id, null, null, 10));
    }

    [Fact]
    public void Endpoints_db_from_2_0_2_gets_the_file_columns()
    {
        var path = Path.Combine(_root, "old-endpoints.db");
        Directory.CreateDirectory(_root);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE endpoints(id INTEGER PRIMARY KEY AUTOINCREMENT, machine_id TEXT NOT NULL UNIQUE COLLATE NOCASE, device_id INTEGER,
                    name TEXT NOT NULL COLLATE NOCASE, domain TEXT COLLATE NOCASE, fqdn TEXT, token_hash TEXT, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL,
                    last_ip TEXT, script_version TEXT, os TEXT, os_build TEXT, model TEXT, serial TEXT, user_name TEXT, firewall_off TEXT, av_problem TEXT,
                    missing_updates INTEGER, last_patch TEXT, pending_reboot INTEGER, worst_disk_pct REAL, process_count INTEGER NOT NULL DEFAULT 0,
                    software_count INTEGER NOT NULL DEFAULT 0, errors TEXT, report BLOB);
                INSERT INTO endpoints(machine_id, name, first_seen, last_seen) VALUES ('old-machine-0001', 'OLDPC', '2026-11-25T00:00:00.000Z', '2026-11-25T00:00:00.000Z');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new EndpointStore(path, _sp.GetRequiredService<NetWatch.Core.Abstractions.ISecretProtector>());
        var row = store.FindByMachine("old-machine-0001")!;
        Assert.Null(row.FileCount);
        Assert.Empty(store.Files(row.Id, null, null, 10));
    }

    // ---------------------------------------------------------------- remediation (quarantine) actions

    private async Task EnableRemediationAsync()
    {
        var settings = _sp.GetRequiredService<SettingsService>();
        var current = settings.Endpoints;
        current.AllowRemediation = true;
        await settings.SaveAsync(EndpointSettings.Key, current, null, CancellationToken.None);
    }

    [Fact]
    public async Task Quarantine_is_handed_out_only_when_enabled_then_confirmed_on_the_next_report()
    {
        var enrolled = await Send(Report(5), machine: "machine-005-0000");
        var id = enrolled.EndpointId!.Value;
        var path = Store.Files(id, "budget", null, 10).First().Path;

        // Disabled by default: a queued action is not handed out.
        var actionId = await Store.QueueActionAsync(id, "quarantine", path, "admin", _clock.GetUtcNow().UtcDateTime);
        _clock.Advance(TimeSpan.FromMinutes(30));
        var offRun = await Send(Report(5), machine: "machine-005-0000", token: enrolled.NewToken);
        Assert.True(offRun.Actions is null or { Count: 0 });
        Assert.Equal("pending", Store.ActionsFor(id, 10).Single(a => a.Id == actionId).Status);

        // Enabled: the action is handed out and marked sent.
        await EnableRemediationAsync();
        _clock.Advance(TimeSpan.FromMinutes(30));
        var handout = await Send(Report(5), machine: "machine-005-0000", token: enrolled.NewToken);
        Assert.Contains(handout.Actions!, a => a.Id == actionId && a.Kind == "quarantine" && a.TargetPath == path);
        Assert.Equal("sent", Store.ActionsFor(id, 10).Single(a => a.Id == actionId).Status);

        // The machine reports the result; it must not be handed out again.
        var done = Report(5);
        done.ActionResults.Add(new EpActionResult { Id = actionId, Ok = true, Message = "Moved to C:\\ProgramData\\HIT SIEM\\Endpoint\\Quarantine\\x", Utc = _clock.GetUtcNow().UtcDateTime });
        _clock.Advance(TimeSpan.FromMinutes(30));
        var after = await Send(done, machine: "machine-005-0000", token: enrolled.NewToken);
        Assert.True(after.Actions is null or { Count: 0 });
        var finished = Store.ActionsFor(id, 10).Single(a => a.Id == actionId);
        Assert.Equal("done", finished.Status);
        Assert.True(finished.Ok);
        Assert.Equal(0, Store.PendingActionCount(id));
    }

    [Fact]
    public void Action_results_on_the_wire_deserialize_into_the_report()
    {
        // The exact JSON shape the script sends (plain array, camelCase) must populate ActionResults.
        const string json = """
            {"schema":1,"system":{"computerName":"PC"},"files":[],
             "actionResults":[{"id":3,"ok":true,"message":"Moved to C:\\...","utc":"2026-11-27T09:00:00Z"},
                              {"id":4,"ok":false,"message":"Access denied","utc":"2026-11-27T09:01:00Z"}]}
            """;
        var report = System.Text.Json.JsonSerializer.Deserialize<EndpointReport>(json, EndpointStore.Json)!;
        Assert.Equal(2, report.ActionResults.Count);
        Assert.Equal(3, report.ActionResults[0].Id);
        Assert.True(report.ActionResults[0].Ok);
        Assert.False(report.ActionResults[1].Ok);
        Assert.Equal("Access denied", report.ActionResults[1].Message);
    }

    [Fact]
    public async Task A_pending_action_can_be_cancelled_but_a_sent_one_cannot()
    {
        await EnableRemediationAsync();
        var enrolled = await Send(Report(5), machine: "machine-005-0000");
        var id = enrolled.EndpointId!.Value;
        var path = Store.Files(id, null, "Downloads", 50).First().Path;

        var a1 = await Store.QueueActionAsync(id, "quarantine", path, "admin", _clock.GetUtcNow().UtcDateTime);
        Assert.True(await Store.CancelActionAsync(id, a1));
        Assert.Equal("cancelled", Store.ActionsFor(id, 10).Single(a => a.Id == a1).Status);

        var a2 = await Store.QueueActionAsync(id, "quarantine", path, "admin", _clock.GetUtcNow().UtcDateTime);
        _clock.Advance(TimeSpan.FromMinutes(30));
        await Send(Report(5), machine: "machine-005-0000", token: enrolled.NewToken); // hands it out → sent
        Assert.False(await Store.CancelActionAsync(id, a2));
        Assert.Equal("sent", Store.ActionsFor(id, 10).Single(a => a.Id == a2).Status);

        // Deleting the machine removes its actions.
        await Store.DeleteAsync(id);
        Assert.Empty(Store.ActionsFor(id, 10));
    }

    // ---------------------------------------------------------------- forwarded endpoint events (WEF)

    private static string EventXml(string channel, int id, string data, long record = 100, string computer = "SIM-PC-003.corp.sim", string provider = "Microsoft-Windows-Security-Auditing") => $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System><Provider Name="{provider}"/><EventID>{id}</EventID><Level>0</Level>
            <TimeCreated SystemTime="2026-11-25T08:00:00.000Z"/><EventRecordID>{record}</EventRecordID>
            <Channel>{channel}</Channel><Computer>{computer}</Computer></System>
          <EventData>{data}</EventData>
        </Event>
        """;

    [Fact]
    public void Process_start_with_an_attack_command_line_is_suspicious()
    {
        var e = EndpointEventCatalog.Parse(EventXml("Security", 4688,
            "<Data Name='SubjectUserName'>user003</Data><Data Name='SubjectDomainName'>CORP</Data><Data Name='NewProcessName'>C:\\Windows\\System32\\vssadmin.exe</Data><Data Name='CommandLine'>vssadmin.exe delete shadows /all /quiet</Data><Data Name='ParentProcessName'>C:\\Windows\\System32\\cmd.exe</Data>".Replace('\'', '"')))!;
        var info = EndpointEventCatalog.Describe(e);
        Assert.Equal("true", info.Fields["suspicious"]);
        Assert.Equal("vssadmin.exe", info.Fields["process"]);
        Assert.Equal("CORP\\user003", info.Fields["user"]);
        Assert.Equal(Severity.Major, info.Severity);

        var normal = EndpointEventCatalog.Describe(EndpointEventCatalog.Parse(EventXml("Security", 4688,
            "<Data Name=\"NewProcessName\">C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe</Data><Data Name=\"CommandLine\">chrome.exe --type=renderer</Data>"))!);
        Assert.Equal("false", normal.Fields["suspicious"]);

        Assert.Matches(EndpointEventCatalog.SuspiciousCommand(), "powershell.exe -nop -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA");
        Assert.Matches(EndpointEventCatalog.SuspiciousCommand(), "rundll32.exe C:\\windows\\System32\\comsvcs.dll, MiniDump 624 C:\\temp\\lsass.dmp full");
        Assert.DoesNotMatch(EndpointEventCatalog.SuspiciousCommand(), "powershell.exe -ExecutionPolicy Bypass -File \\\\corp\\NETLOGON\\HitSiem\\HitSiemInventory.ps1");
    }

    [Fact]
    public void Local_admin_usb_malware_and_unknown_events()
    {
        var admin = EndpointEventCatalog.Describe(EndpointEventCatalog.Parse(EventXml("Security", 4732,
            "<Data Name=\"MemberSid\">S-1-5-21-1-2-3-1105</Data><Data Name=\"TargetUserName\">Administratoren</Data><Data Name=\"TargetSid\">S-1-5-32-544</Data><Data Name=\"SubjectUserName\">helpdesk</Data><Data Name=\"SubjectDomainName\">CORP</Data>"))!);
        Assert.Equal("true", admin.Fields["privileged"]); // by SID, so a German "Administratoren" group counts too
        Assert.Equal(Severity.Major, admin.Severity);

        var usb = EndpointEventCatalog.Describe(EndpointEventCatalog.Parse(EventXml("Security", 6416,
            "<Data Name=\"DeviceId\">USBSTOR\\DISK&amp;VEN_SANDISK\\4C53</Data><Data Name=\"DeviceDescription\">SanDisk Cruzer</Data><Data Name=\"ClassName\">DiskDrive</Data>"))!);
        Assert.Equal("true", usb.Fields["usbStorage"]);

        var malware = EndpointEventCatalog.Describe(EndpointEventCatalog.Parse(EventXml(EndpointEventCatalog.Defender, 1116,
            "<Data Name=\"Threat Name\">Trojan:Win32/Wacatac</Data><Data Name=\"Path\">file:_C:\\Users\\u\\Downloads\\x.exe</Data><Data Name=\"Detection User\">CORP\\u</Data>",
            provider: "Microsoft-Windows-Windows Defender"))!);
        Assert.Equal(Severity.Critical, malware.Severity);
        Assert.Equal("Trojan:Win32/Wacatac", malware.Fields["threat"]);

        var msi = EndpointEventCatalog.Describe(EndpointEventCatalog.Parse(EventXml("Application", 11707, "<Data>Product: AnyDesk -- Installation completed successfully.</Data>", provider: "MsiInstaller"))!);
        Assert.Equal("AnyDesk", msi.Fields["product"]);

        Assert.Null(EndpointEventCatalog.Parse(EventXml("Security", 4624, "<Data Name=\"TargetUserName\">x</Data>")));
        Assert.Null(EndpointEventCatalog.Parse("<not xml"));
        Assert.Equal("Firewall", EndpointEventCatalog.ShortLog(EndpointEventCatalog.Firewall));
    }

    [Fact]
    public async Task Forwarded_events_are_stored_once_and_signalled()
    {
        var device = Inventory.GetSnapshot((await Send(Report(3))).DeviceId!.Value)!;
        var readings = new[]
        {
            EndpointEventCatalog.Parse(EventXml("Security", 4688, "<Data Name=\"NewProcessName\">C:\\x\\mimikatz.exe</Data><Data Name=\"CommandLine\">mimikatz.exe sekurlsa::logonpasswords</Data>", 501))!,
            EndpointEventCatalog.Parse(EventXml("Security", 4688, "<Data Name=\"NewProcessName\">C:\\Windows\\notepad.exe</Data>", 502))!,
            EndpointEventCatalog.Parse(EventXml("System", 7045, "<Data Name=\"ServiceName\">PSEXESVC</Data><Data Name=\"ImagePath\">%SystemRoot%\\PSEXESVC.exe</Data>", 77, provider: "Service Control Manager"))!,
        };
        using var scope = _sp.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<EventsDbContext>();

        // Process starts not stored: only the suspicious one is kept, all are signalled.
        Assert.Equal(2, await EndpointEventCatalog.IngestAsync(device, readings, storeProcessStarts: false, events, _signals, CancellationToken.None));
        Assert.Equal(0, await EndpointEventCatalog.IngestAsync(device, readings, storeProcessStarts: false, events, _signals, CancellationToken.None));
        var stored = await events.WindowsEvents.Where(w => w.DeviceId == device.Id).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, w => w.LogName == "Security" && w.EventId == 4688 && w.Message.Contains("mimikatz", StringComparison.Ordinal));
        Assert.Contains(stored, w => w.LogName == "System" && w.EventId == 7045);
        Assert.Contains(_signals.All.OfType<EventSignal>(), s => s.EventKey == EventKeys.EndpointEvent && s.Fields!["suspicious"] == "true");
    }

    // ---------------------------------------------------------------- default rules, kit, script

    [Fact]
    public void Default_rules_cover_the_endpoint_signals()
    {
        var rules = NetWatch.Alerts.DefaultRules.Create(DateTime.UtcNow);
        foreach (var key in new[] { StateKeys.EndpointStale, StateKeys.EndpointFirewallOff, StateKeys.EndpointAntivirus, MetricKeys.EndpointMissingUpdates, MetricKeys.EndpointPatchAgeDays })
        {
            Assert.Contains(rules, r => r.Key == key);
        }

        var change = rules.Where(r => r.Key == EventKeys.EndpointChange).Select(r => EventFilter.Parse(r.FilterJson).FieldEquals["change"]).ToList();
        Assert.Contains(ChangeKinds.SoftwareInstalled, change);
        Assert.Contains(ChangeKinds.LocalAdminAdded, change);
        Assert.Contains(ChangeKinds.UsbStorage, change);

        // Every event ID an endpoint rule filters on is in the WEF subscription.
        var forwarded = EndpointEventCatalog.Events.Values.SelectMany(v => v).ToHashSet();
        foreach (var rule in rules.Where(r => r.Key == EventKeys.EndpointEvent))
        {
            Assert.All(EventFilter.Parse(rule.FilterJson).EventIds, id => Assert.Contains(id, forwarded));
        }
    }

    [Fact]
    public void Deployment_kit_contains_script_config_subscription_and_instructions()
    {
        var zip = EndpointKit.Build("https://hitsiem.corp.local:8443", "the-key", "ABCDEF", new EndpointSettings { IntervalMin = 20, CollectCommandLines = false });
        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.Equal(["HitSiem-Endpoints-Query.xml", "HitSiem-Endpoints-Subscription.xml", "HitSiemInventory.ps1", "README.txt", "hitsiem-endpoint.json"],
            archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());

        string Read(string name)
        {
            using var r = new StreamReader(archive.GetEntry(name)!.Open());
            return r.ReadToEnd();
        }

        var config = System.Text.Json.JsonDocument.Parse(Read("hitsiem-endpoint.json")).RootElement;
        Assert.Equal("https://hitsiem.corp.local:8443", config.GetProperty("server").GetString());
        Assert.Equal("the-key", config.GetProperty("enrollmentKey").GetString());
        Assert.Equal("ABCDEF", config.GetProperty("thumbprint").GetString());
        Assert.False(config.GetProperty("collectCommandLines").GetBoolean());
        Assert.True(config.GetProperty("collectFiles").GetBoolean());
        Assert.Equal(20000, config.GetProperty("maxFiles").GetInt32());

        var readme = Read("README.txt");
        Assert.DoesNotContain("{SERVER}", readme, StringComparison.Ordinal);
        Assert.Contains("Server=http://hitsiem.corp.local:5985/wsman/SubscriptionManager/WEC", readme, StringComparison.Ordinal);
        Assert.Contains("every 20 minutes", readme, StringComparison.Ordinal);

        var subscription = XDocument.Parse(Read("HitSiem-Endpoints-Subscription.xml"));
        XNamespace ns = "http://schemas.microsoft.com/2006/03/windows/events/subscription";
        Assert.Equal("SourceInitiated", subscription.Root!.Element(ns + "SubscriptionType")!.Value);
        var query = XDocument.Parse(subscription.Root.Element(ns + "Query")!.Value);
        Assert.Equal(EndpointEventCatalog.Events.Count, query.Descendants("Select").Count());
        Assert.Contains(query.Descendants("Select"), s => s.Attribute("Path")!.Value == "Security" && s.Value.Contains("EventID=4688", StringComparison.Ordinal));

        Assert.Contains("HIT SIEM endpoint inventory", Read("HitSiemInventory.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void Script_parses_in_Windows_PowerShell_5_1_and_has_no_internet_endpoints()
    {
        var path = Path.Combine(_root, "HitSiemInventory.ps1");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, EndpointKit.Script());
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('{path}', [ref]$null, [ref]$e); if ($e.Count) {{ $e | ForEach-Object {{ $_.ToString() }}; exit 1 }}; exit 0\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        Assert.True(p.ExitCode == 0, output);

        var text = System.Text.Encoding.UTF8.GetString(EndpointKit.Script());
        Assert.DoesNotMatch(@"https?://(?!localhost)[a-z0-9.-]+\.(com|net|org|io)", text);
        Assert.DoesNotContain("Invoke-WebRequest", text, StringComparison.Ordinal);
        Assert.Contains("$searcher.Online = $false", text, StringComparison.Ordinal);
    }

    private sealed class Clock(DateTime start) : TimeProvider
    {
        private long _ticks = start.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class RecordingSink : IAlertSignalSink
    {
        private readonly ConcurrentQueue<AlertSignal> _signals = new();

        public IReadOnlyList<AlertSignal> All => _signals.ToArray();

        public void Publish(AlertSignal signal) => _signals.Enqueue(signal);
    }
}
