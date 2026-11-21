using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetWatch.Core;
using NetWatch.Core.Abstractions;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data;
using NetWatch.Data.Config;
using NetWatch.Data.Events;
using NetWatch.Web.Reports;

namespace NetWatch.Tests;

/// <summary>WP7: capacity estimates, the VMware report, the extended capacity/security/interface reports and rendering.</summary>
public sealed class Wp7ReportTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netwatch-wp7-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;

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
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<ReportDocument> BuildAsync(ReportType type, int days = 7)
    {
        using var scope = _sp.CreateScope();
        var p = scope.ServiceProvider;
        var builder = new ReportBuilder(p.GetRequiredService<NetWatchDbContext>(), p.GetRequiredService<EventsDbContext>(), p.GetRequiredService<IMetricStore>(), p.GetRequiredService<SettingsService>());
        return await builder.BuildAsync(new ReportRequest(type, DateTime.UtcNow.AddDays(-days), DateTime.UtcNow), CancellationToken.None);
    }

    [Theory]
    [InlineData(80, 7, 7, 20.0)]     // +1 pt/day, 20 pt left → 20 days
    [InlineData(99, 14, 7, 0.5)]
    [InlineData(50, 0.005, 7, null)]  // flat
    [InlineData(50, -3, 7, null)]     // shrinking
    [InlineData(100, 1, 1, 0.0)]
    public void Days_until_full(double used, double growth, double periodDays, double? expected) =>
        Assert.Equal(expected, CapacityMath.DaysUntilFull(used, growth, periodDays));

    [Fact]
    public void Trend_is_second_half_minus_first_half()
    {
        Assert.Equal(20, CapacityMath.Trend([10, 10, 30, 30]));
        Assert.Equal(-5, CapacityMath.Trend([50, 50, 50, 45, 45, 45]));
        Assert.Null(CapacityMath.Trend([1, 2, 3]));
    }

    [Fact]
    public async Task Vmware_report_lists_hosts_datastores_snapshots_and_powered_off_vms()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetWatchDbContext>();
            var now = DateTime.UtcNow;
            var vc = new Device { Name = "VC-01", IpAddress = "10.0.0.50", Type = DeviceType.VCenter, CreatedUtc = now, UpdatedUtc = now, StatusSinceUtc = now };
            db.Devices.Add(vc);
            await db.SaveChangesAsync();
            db.VmwareHosts.Add(new VmwareHost { SourceDeviceId = vc.Id, MoRef = "host-1", Name = "esx01", ConnectionState = "connected", CpuTotalMhz = 100_000, CpuUsageMhz = 41_000, MemTotalBytes = 512L << 30, MemUsageBytes = 256L << 30, LastSeenUtc = now });
            db.VmwareHosts.Add(new VmwareHost { SourceDeviceId = vc.Id, MoRef = "host-2", Name = "esx02", ConnectionState = "notResponding", LastSeenUtc = now });
            db.VmwareDatastores.Add(new VmwareDatastore { SourceDeviceId = vc.Id, MoRef = "ds-1", Name = "DS-SSD-01", Type = "VMFS", CapacityBytes = 4L << 40, FreeBytes = 1L << 40, HostCount = 2, LastSeenUtc = now });
            db.VmwareVms.Add(new VmwareVm { SourceDeviceId = vc.Id, MoRef = "vm-1", Name = "APP-01", PowerState = "poweredOn", HostMoRef = "host-1", LastSeenUtc = now });
            db.VmwareVms.Add(new VmwareVm { SourceDeviceId = vc.Id, MoRef = "vm-2", Name = "OLD-ERP", PowerState = "poweredOff", HostName = "esx01", NumCpu = 4, MemoryMb = 16384, LastSeenUtc = now });
            db.VmwareSnapshots.Add(new VmwareSnapshot { SourceDeviceId = vc.Id, VmMoRef = "vm-1", VmName = "APP-01", MoRef = "snap-1", Name = "before patch", CreatedUtc = now.AddDays(-12), SizeBytes = 64L << 30, LastSeenUtc = now });
            await db.SaveChangesAsync();
        }

        var doc = await BuildAsync(ReportType.Vmware);
        Assert.Equal("VMware", doc.Title);
        var summary = doc.Sections[0].Summary.ToDictionary(x => x.Label, x => x.Value);
        Assert.Equal(("2", "1", "1", "1"), (summary["ESXi hosts"], summary["Hosts not connected"], summary["Powered off"], summary["Snapshots older than 7 days"]));
        var hosts = doc.Sections.Single(s => s.Heading == "ESXi hosts").Table!;
        Assert.Equal("41.0 %", hosts.Rows.Single(r => r[0] == "esx01")[5]);
        Assert.Equal("75.0 %", doc.Sections.Single(s => s.Heading == "Datastore capacity").Table!.Rows.Single()[4]);
        Assert.Equal("12", doc.Sections.Single(s => s.Heading.StartsWith("Snapshots", StringComparison.Ordinal)).Table!.Rows.Single()[3]);
        Assert.Equal("OLD-ERP", doc.Sections.Single(s => s.Heading.StartsWith("Powered-off", StringComparison.Ordinal)).Table!.Rows.Single()[0]);

        // Every report renders to PDF and CSV.
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(PdfReportRenderer.Render(doc)[..8]));
        Assert.Contains("OLD-ERP", System.Text.Encoding.UTF8.GetString(CsvReportRenderer.Render(doc)));
    }

    [Fact]
    public async Task Extended_capacity_security_and_interface_reports_have_their_sections()
    {
        var capacity = await BuildAsync(ReportType.Capacity);
        Assert.Contains(capacity.Sections, s => s.Heading == "Predicted to fill within 30 days");
        Assert.Contains(capacity.Sections[0].Summary, x => x.Label == "Full within 30 days");

        var security = await BuildAsync(ReportType.Security);
        Assert.Contains(security.Sections, s => s.Heading == "Admin logons outside working hours");
        Assert.Contains(security.Sections, s => s.Heading == "Security log cleared / audit policy changed");

        Assert.Equal(7, ReportCatalog.Types.Count);
        Assert.Contains(ReportCatalog.Types, t => t.Type == ReportType.Vmware && !t.HasPeriod);
    }
}
