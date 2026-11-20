using System.Text;
using NetWatch.Collectors.Simulator;
using NetWatch.Collectors.Snmp;
using NetWatch.Core;
using NetWatch.Core.Credentials;
using NetWatch.Web.Auth;
using NetWatch.Web.Csv;
using NetWatch.Web.Discovery;

namespace NetWatch.Tests;

public class ParserTests
{
    private static DeviceCsvParseResult Parse(string csv) => DeviceCsvParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(csv)));

    [Fact]
    public void Csv_TemplateParses()
    {
        var result = Parse(DeviceCsvParser.Template);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Rows.Count);
        var first = result.Rows[0];
        Assert.Equal("CORE-SW-01", first.Name);
        Assert.Equal("10.0.0.1", first.IpAddress);
        Assert.Equal(DeviceType.Switch, first.Type);
        Assert.Equal("HQ/Data Center/Rack 01", first.GroupPath);
        Assert.Equal(["core", "cisco"], first.Tags);
        Assert.Equal(60, first.PollIntervalSec);
        Assert.Equal("CORE-SW-01", result.Rows[1].Parent);
        Assert.Equal(DeviceType.Server, result.Rows[2].Type);
    }

    [Fact]
    public void Csv_ReportsBadRowsAndKeepsGoodOnes()
    {
        var result = Parse("Name;IP;Type;Poll Interval\nA;10.0.0.1;DC;30\nB;not-an-ip;Switch;\nC;10.0.0.3;Toaster;\nD;10.0.0.4;ESXi;5\nE;10.0.0.5;;\n");
        Assert.Equal(["A", "E"], result.Rows.Select(r => r.Name));
        Assert.Equal(DeviceType.DomainController, result.Rows[0].Type);
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Message.Contains("not-an-ip"));
        Assert.Contains(result.Errors, e => e.Message.Contains("Toaster"));
        Assert.Contains(result.Errors, e => e.Message.Contains("poll_interval"));
    }

    [Fact]
    public void Csv_MissingRequiredColumns()
    {
        var result = Parse("hostname,address\nx,1.2.3.4\n");
        Assert.Empty(result.Rows);
        Assert.Single(result.Errors);
    }

    [Theory]
    [InlineData("10.0.1.0/24", 254)]
    [InlineData("10.0.1.77/24", 254)]
    [InlineData("10.0.1.0/30", 2)]
    [InlineData("10.0.1.0/31", 2)]
    [InlineData("10.0.1.9", 1)]
    [InlineData("10.0.0.0/22", 1022)]
    public void Discovery_ExpandsCidr(string cidr, int expected)
    {
        Assert.True(DiscoveryService.TryExpand(cidr, out var hosts, out _));
        Assert.Equal(expected, hosts.Count);
    }

    [Fact]
    public void Discovery_ExpandsToCorrectAddresses()
    {
        Assert.True(DiscoveryService.TryExpand("192.168.5.130/30", out var hosts, out _));
        Assert.Equal(["192.168.5.129", "192.168.5.130"], hosts.Select(h => h.ToString()));
    }

    [Theory]
    [InlineData("10.0.0.0/21")]
    [InlineData("10.0.0.0/33")]
    [InlineData("fe80::1/64")]
    [InlineData("garbage")]
    public void Discovery_RejectsInvalidRanges(string cidr) => Assert.False(DiscoveryService.TryExpand(cidr, out _, out _));

    [Theory]
    [InlineData("1.3.6.1.4.1.9.1.2494", "Cisco")]
    [InlineData(".1.3.6.1.4.1.11.2.3.7.11.181", "HP")]
    [InlineData("1.3.6.1.4.1.2636.1.1.1.2.63", "Juniper")]
    [InlineData("1.3.6.1.4.1.47196.4.1.1.1.4", "HPE Aruba")]
    [InlineData("1.3.6.1.4.1.99.1", null)]
    [InlineData(null, null)]
    public void Vendor_FromSysObjectId(string? oid, string? vendor) => Assert.Equal(vendor, VendorCatalog.VendorFromSysObjectId(oid));

    [Theory]
    [InlineData("Cisco IOS Software [Cupertino], Catalyst L3 Switch Software (CAT9K_IOSXE), Version 17.9.4a, RELEASE SOFTWARE (fc5)", "17.9.4a")]
    [InlineData("Aruba JL254A 2930F-48G-4SFP+ Switch, revision WC.16.11.0012, ROM WC.16.01.0009", "WC.16.11.0012")]
    [InlineData("Juniper Networks, Inc. ex4300-48t Ethernet Switch, kernel JUNOS 21.4R3-S5, Build date: 2023", "21.4R3-S5")]
    [InlineData("Linux nms 5.15.0", null)]
    public void Firmware_FromSysDescr(string descr, string? expected) => Assert.Equal(expected, SystemInfoProbe.FirmwareFromDescription(descr));

    [Theory]
    [InlineData(42, 9, 0, 42)]
    [InlineData(425, 9, 1, 42.5)]
    [InlineData(42000, 8, 0, 42)]
    public void EntitySensor_Scaling(double raw, int scale, int precision, double expected) =>
        Assert.Equal(expected, HealthCollector.EntitySensorValue(raw, scale, precision), 6);

    [Theory]
    [InlineData("Short1!", false)]
    [InlineData("alllowercaseletters", false)]
    [InlineData("Correct-Horse-42", true)]
    [InlineData("admin-Password-1", false)]
    public void PasswordPolicy(string password, bool ok) => Assert.Equal(ok, PasswordService.Validate(password, "admin", 10) is null);

    [Fact]
    public void GeneratedPasswordsMeetPolicy()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.Null(PasswordService.Validate(PasswordService.Generate(), "admin", 10));
        }
    }

    [Theory]
    [InlineData(CredentialKind.SnmpV2c, true)]
    [InlineData(CredentialKind.SnmpV3, false)]
    public void CredentialValidation(CredentialKind kind, bool valid)
    {
        var secret = new CredentialSecret { Community = "public" };
        Assert.Equal(valid, secret.Validate(kind) is null);
    }

    [Fact]
    public void CredentialV3_RequiresAuthForPrivacy()
    {
        var secret = new CredentialSecret { Username = "nms", AuthProtocol = SnmpAuthProtocol.None, PrivProtocol = SnmpPrivProtocol.AES128, PrivPassword = "12345678" };
        Assert.NotNull(secret.Validate(CredentialKind.SnmpV3));
        secret.AuthProtocol = SnmpAuthProtocol.SHA256;
        secret.AuthPassword = "authpass1";
        Assert.Null(secret.Validate(CredentialKind.SnmpV3));
        Assert.DoesNotContain("authpass1", secret.BuildDisplayHint(CredentialKind.SnmpV3));
    }

    [Fact]
    public void SimulatorDiurnalCurve()
    {
        // 13:00 PKT on a Wednesday = 08:00 UTC: business hours, high load. 03:00 PKT: low.
        Assert.True(SimulatedDevicePoller.Diurnal(new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc)) > 0.8);
        Assert.True(SimulatedDevicePoller.Diurnal(new DateTime(2026, 11, 17, 22, 0, 0, DateTimeKind.Utc)) < 0.2);
    }
}
