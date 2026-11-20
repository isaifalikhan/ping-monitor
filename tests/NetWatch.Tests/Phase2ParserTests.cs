using System.Net;
using System.Text;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using NetWatch.Collectors.Receivers;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Servers.Linux;
using NetWatch.Collectors.Servers.Windows;
using NetWatch.Core;
using NetWatch.Core.Configuration;
using NetWatch.Core.Entities;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;

namespace NetWatch.Tests;

/// <summary>Phase 2 parsers: syslog, TCP framing, traps, Linux/Windows server data, maintenance schedules, LDAP helpers.</summary>
public class Phase2ParserTests
{
    // ---------------------------------------------------------------- syslog

    [Fact]
    public void Syslog_Rfc5424WithStructuredData()
    {
        var m = SyslogParser.Parse("<165>1 2026-11-20T08:14:15.003Z core-sw-01 mgd 4321 UI_COMMIT [junos@2636 user=\"ad\\]min\"] User 'admin' requested 'commit'");
        Assert.Equal(20, m.Facility);
        Assert.Equal(5, m.Severity);
        Assert.Equal(new DateTime(2026, 11, 20, 8, 14, 15, 3, DateTimeKind.Utc), m.MessageUtc);
        Assert.Equal("core-sw-01", m.Hostname);
        Assert.Equal("mgd", m.AppName);
        Assert.Equal("4321", m.ProcId);
        Assert.Equal("UI_COMMIT", m.MsgId);
        Assert.Equal("User 'admin' requested 'commit'", m.Message);
    }

    [Fact]
    public void Syslog_Rfc3164WithHostAndTag()
    {
        var m = SyslogParser.Parse("<86>Nov 20 13:05:01 web01 sshd[2211]: Failed password for root from 10.1.1.9 port 50122 ssh2");
        Assert.Equal(10, m.Facility);
        Assert.Equal(6, m.Severity);
        Assert.Null(m.MessageUtc);
        Assert.Equal("web01", m.Hostname);
        Assert.Equal("sshd", m.AppName);
        Assert.Equal("2211", m.ProcId);
        Assert.StartsWith("Failed password for root", m.Message);
    }

    [Fact]
    public void Syslog_CiscoSequenceNumberAndMnemonic()
    {
        var m = SyslogParser.Parse("<187>1234: *Nov 20 13:05:01.123 PKT: %LINK-3-UPDOWN: Interface GigabitEthernet1/0/5, changed state to down");
        Assert.Equal(3, m.Severity);
        Assert.Equal("LINK-3-UPDOWN", m.AppName);
        Assert.Equal("Interface GigabitEthernet1/0/5, changed state to down", m.Message);
    }

    [Fact]
    public void Syslog_HpProcurveFormat()
    {
        var m = SyslogParser.Parse("<14>Nov 20 13:05:01 HP-SW-07 00076 ports: port 12 is now off-line");
        Assert.Equal("HP-SW-07", m.Hostname);
        Assert.Contains("port 12 is now off-line", m.Message);
    }

    [Fact]
    public void Syslog_MissingPriAndControlCharacters()
    {
        var m = SyslogParser.Parse("plain text\u0007 without header\n");
        Assert.Equal(1, m.Facility);
        Assert.Equal(5, m.Severity);
        Assert.Equal("plain text  without header", m.Message);
    }

    [Fact]
    public void TcpFraming_OctetCountingAndNewlinesAndPartialMessages()
    {
        var buffer = new MemoryStream();
        void Write(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            buffer.Write(b, 0, b.Length);
        }

        Write("11 <13>hello A<14>second line\n<15>partial");
        var first = TcpFraming.Extract(buffer, 65536);
        Assert.Equal(["<13>hello A", "<14>second line"], first);
        Assert.Equal("<15>partial", Encoding.UTF8.GetString(buffer.ToArray()));

        Write(" done\n");
        Assert.Equal(["<15>partial done"], TcpFraming.Extract(buffer, 65536));
        Assert.Equal(0, buffer.Length);
    }

    // ---------------------------------------------------------------- traps

    private static DecodedTrap RoundTrip(ISnmpMessage message) =>
        TrapDecoder.Decode(MessageFactory.ParseMessages(message.ToBytes(), new UserRegistry())[0])!;

    [Fact]
    public void Trap_V2cLinkDownIsDecodedWithIfIndex()
    {
        var trap = new TrapV2Message(1, VersionCode.V2, new OctetString("public"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.3"), 1000,
        [
            new Variable(new ObjectIdentifier("1.3.6.1.2.1.2.2.1.1.17"), new Integer32(17)),
            new Variable(new ObjectIdentifier("1.3.6.1.2.1.2.2.1.8.17"), new Integer32(2)),
        ]);
        var d = RoundTrip(trap);
        Assert.Equal("v2c", d.Version);
        Assert.Equal("linkDown", d.Name);
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Equal(17, d.IfIndex);
        Assert.True(d.IsLinkEvent);
        Assert.Equal("linkDown: Gi1/0/17 (ifIndex 17)", d.Summary("Gi1/0/17"));
        Assert.Contains("ifOperStatus", d.VarbindsJson());
    }

    [Fact]
    public void Trap_V1GenericAndEnterpriseSpecific()
    {
        var cold = new TrapV1Message(VersionCode.V1, IPAddress.Parse("10.0.0.5"), new OctetString("public"), new ObjectIdentifier("1.3.6.1.4.1.9"),
            GenericCode.ColdStart, 0, 0, new List<Variable>());
        var d = RoundTrip(cold);
        Assert.Equal("coldStart", d.Name);
        Assert.True(d.IsRestart);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), d.AgentAddress);

        var specific = new TrapV1Message(VersionCode.V1, IPAddress.Parse("10.0.0.5"), new OctetString("public"), new ObjectIdentifier("1.3.6.1.4.1.2636.4.1"),
            GenericCode.EnterpriseSpecific, 2, 0, new List<Variable>());
        var j = RoundTrip(specific);
        Assert.Equal("1.3.6.1.4.1.2636.4.1.0.2", j.TrapOid);
    }

    [Fact]
    public void Trap_CatalogKnowsVendorTraps()
    {
        Assert.Equal(("jnxFanFailure", Severity.Major), TrapCatalog.Lookup("1.3.6.1.4.1.2636.4.1.2"));
        Assert.Equal(("ciscoEnvMonTemperatureNotification", Severity.Major), TrapCatalog.Lookup("1.3.6.1.4.1.9.9.13.3.0.3"));
        Assert.Equal((null, Severity.Info), TrapCatalog.Lookup("1.3.6.1.4.1.99999.1"));
        Assert.Equal("ifName", TrapCatalog.VarbindName("1.3.6.1.2.1.31.1.1.1.1.5"));
    }

    // ---------------------------------------------------------------- Linux

    private const string LinuxOutput = """
        ##STAT1
        cpu  1000 0 500 8000 500 0 0 0 0 0
        ##STAT2
        cpu  1100 0 550 8300 550 0 0 0 0 0
        ##MEM
        MemTotal:       16000000 kB
        MemFree:         2000000 kB
        MemAvailable:    4000000 kB
        ##UP
        86400.50 300000.00
        ##LOAD
        0.50 1.20 0.90 2/345 6789
        ##NPROC
        4
        ##DF
        Filesystem     Type 1024-blocks     Used Available Capacity Mounted on
        /dev/sda1      xfs     52403200 31441920  20961280      61% /
        /dev/sdb1      ext4   104857600 99614720   5242880      95% /var/lib/my data
        ##NET
        Inter-|   Receive                                                |  Transmit
         face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
            lo: 9999999   1000    0    0    0     0          0         0  9999999   1000    0    0    0     0       0          0
          eth0: 1000000   2000    0    0    0     0          0         0   500000   1500    0    0    0     0       0          0
        ##OS
        Ubuntu 22.04.4 LTS
        5.15.0-105-generic
        ##SVC
        Id=sshd.service
        LoadState=loaded
        ActiveState=active
        Description=OpenBSD Secure Shell server

        Id=nginx.service
        LoadState=loaded
        ActiveState=failed
        Description=A high performance web server

        Id=foo.service
        LoadState=not-found
        ActiveState=inactive
        Description=foo.service
        ##REBOOT
        yes
        """;

    [Fact]
    public void Linux_ParsesAllSections()
    {
        var data = new ServerPollData();
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        LinuxStatsParser.Parse(LinuxOutput, ["sshd", "nginx", "foo"], now, data);

        // CPU: total delta 500, idle(+iowait) delta 350 → 30 % busy.
        Assert.Equal(30, data.CpuPct);
        Assert.Equal(75, data.MemPct);
        Assert.Equal(16000000L * 1024, data.MemTotalBytes);
        Assert.Equal(now.AddSeconds(-86400.5), data.LastBootUtc);
        Assert.Equal(1.2, data.Load5);
        Assert.Equal(4, data.CpuCount);
        Assert.Equal(2, data.Volumes.Count);
        Assert.Equal("/var/lib/my data", data.Volumes[1].Name);
        Assert.Equal(95, Math.Round(data.Volumes[1].UsedPct));
        Assert.Equal(1000000UL, data.NetRxBytes); // loopback excluded
        Assert.Equal(500000UL, data.NetTxBytes);
        Assert.Equal("Ubuntu 22.04.4 LTS", data.OsName);
        Assert.Equal("5.15.0-105-generic", data.OsVersion);
        Assert.Equal([ServiceState.Running, ServiceState.Failed, ServiceState.NotFound], data.Services.Select(s => s.State));
        Assert.True(data.PendingReboot);
    }

    [Fact]
    public void Linux_ScriptRejectsUnsafeUnitNames()
    {
        var script = SshServerCollector.BuildScript(["sshd", "evil; rm -rf /", "nginx.service"]);
        Assert.Contains("'sshd'", script);
        Assert.Contains("'nginx.service'", script);
        Assert.DoesNotContain("rm -rf", script);
    }

    // ---------------------------------------------------------------- Windows

    [Fact]
    public void Windows_MapsWmiRows()
    {
        var data = new ServerPollData();
        WindowsStatsParser.ApplyOperatingSystem(
        [
            new WmiRow { ["Caption"] = "Microsoft Windows Server 2022 Standard ", ["Version"] = "10.0.20348", ["LastBootUpTime"] = "20261119083000.500000+300",
                ["TotalVisibleMemorySize"] = "8000000", ["FreePhysicalMemory"] = "2000000" },
        ], data);
        WindowsStatsParser.ApplyDisks([new WmiRow { ["DeviceID"] = "C:", ["VolumeName"] = "System", ["FileSystem"] = "NTFS", ["Size"] = "1000", ["FreeSpace"] = "100" }], data);
        WindowsStatsParser.ApplyServices([new WmiRow { ["Name"] = "Spooler", ["DisplayName"] = "Print Spooler", ["State"] = "Stopped", ["StartMode"] = "Auto" }], ["spooler", "W3SVC"], data);
        WindowsStatsParser.ApplyHotfixes([new WmiRow { ["HotFixID"] = "KB1", ["InstalledOn"] = "3/5/2026" }, new WmiRow { ["HotFixID"] = "KB2", ["InstalledOn"] = "11/1/2026" }], data);
        WindowsStatsParser.ApplyNetwork([new WmiRow { ["Name"] = "Intel NIC", ["BytesReceivedPersec"] = "1000", ["BytesSentPersec"] = "500" }], data);

        Assert.Equal("Microsoft Windows Server 2022 Standard", data.OsName);
        Assert.Equal(new DateTime(2026, 11, 19, 3, 30, 0, 500, DateTimeKind.Utc), data.LastBootUtc);
        Assert.Equal(75, data.MemPct);
        Assert.Equal(90, data.Volumes[0].UsedPct);
        Assert.Equal(ServiceState.Stopped, data.Services[0].State);
        Assert.Equal(ServiceState.NotFound, data.Services[1].State);
        Assert.Equal("KB2", data.LastPatchId);
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), data.LastPatchUtc);
        Assert.Equal(8000, data.NetInBps);
    }

    [Fact]
    public void Windows_WinRmXmlItemsAreParsed()
    {
        const string xml = """
            <p:Win32_OperatingSystem xmlns:p="http://schemas.microsoft.com/wbem/wsman/1/wmi/root/cimv2/Win32_OperatingSystem"
                xmlns:cim="http://schemas.dmtf.org/wbem/wscim/1/common" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <p:Caption>Microsoft Windows Server 2019 Standard</p:Caption>
              <p:LastBootUpTime><cim:Datetime>2026-11-19T08:30:00.5+05:00</cim:Datetime></p:LastBootUpTime>
              <p:OtherTypeDescription xsi:nil="true"/>
              <p:MUILanguages>en-US</p:MUILanguages>
              <p:MUILanguages>de-DE</p:MUILanguages>
            </p:Win32_OperatingSystem>
            """;
        var row = WmiValue.FromXml(xml);
        Assert.Equal("Microsoft Windows Server 2019 Standard", row["Caption"]);
        Assert.Equal(new DateTime(2026, 11, 19, 3, 30, 0, 500, DateTimeKind.Utc), WmiValue.DateTime(row["LastBootUpTime"]));
        Assert.Null(row["OtherTypeDescription"]);
        Assert.Equal("en-US,de-DE", row["MUILanguages"]);
    }

    [Fact]
    public void Windows_AccountSplitsDomain()
    {
        var a = WindowsAccount.From(new Core.Credentials.CredentialSecret { Username = @"CORP\svc-nw", Password = "x" });
        Assert.Equal("CORP", a.Domain);
        Assert.Equal("svc-nw", a.User);
        Assert.Equal(@"CORP\svc-nw", a.QualifiedUser);
        var upn = WindowsAccount.From(new Core.Credentials.CredentialSecret { Username = "svc@corp.local", Domain = "CORP", Password = "x" });
        Assert.Equal("svc@corp.local", upn.QualifiedUser);
    }

    // ---------------------------------------------------------------- maintenance windows

    private static MaintenanceWindow Window(MaintenanceRecurrence r, Action<MaintenanceWindow> setup)
    {
        var w = new MaintenanceWindow { Name = "w", IsEnabled = true, Recurrence = r };
        setup(w);
        return w;
    }

    [Fact]
    public void Maintenance_OnceIsActiveBetweenStartAndEnd()
    {
        var w = Window(MaintenanceRecurrence.Once, x =>
        {
            x.StartUtc = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
            x.EndUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        });
        Assert.False(MaintenanceSchedule.IsActive(w, new DateTime(2026, 11, 20, 9, 59, 0, DateTimeKind.Utc)));
        Assert.True(MaintenanceSchedule.IsActive(w, new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc)));
        Assert.False(MaintenanceSchedule.IsActive(w, new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc)));
        w.IsEnabled = false;
        Assert.False(MaintenanceSchedule.IsActive(w, new DateTime(2026, 11, 20, 11, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Maintenance_DailyUsesPakistanTimeAndSpansMidnight()
    {
        // 23:30 PKT for 60 min = 18:30–19:30 UTC.
        var w = Window(MaintenanceRecurrence.Daily, x =>
        {
            x.StartTimeLocal = "23:30";
            x.DurationMin = 60;
        });
        Assert.False(MaintenanceSchedule.IsActive(w, new DateTime(2026, 11, 20, 18, 29, 0, DateTimeKind.Utc)));
        Assert.True(MaintenanceSchedule.IsActive(w, new DateTime(2026, 11, 20, 18, 30, 0, DateTimeKind.Utc)));
        Assert.True(MaintenanceSchedule.IsActive(w, new DateTime(2026, 11, 20, 19, 15, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 20, 19, 30, 0, DateTimeKind.Utc), MaintenanceSchedule.CurrentEnd(w, new DateTime(2026, 11, 20, 19, 0, 0, DateTimeKind.Utc)));
        Assert.False(MaintenanceSchedule.IsActive(w, new DateTime(2026, 11, 20, 19, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Maintenance_WeeklyOnlyOnSelectedDays()
    {
        // Sundays 02:00 PKT (= Saturday 21:00 UTC) for 2 h. 2026-11-22 is a Sunday.
        var w = Window(MaintenanceRecurrence.Weekly, x =>
        {
            x.StartTimeLocal = "02:00";
            x.DaysOfWeek = "Sun";
            x.DurationMin = 120;
        });
        Assert.True(MaintenanceSchedule.IsActive(w, new DateTime(2026, 11, 21, 21, 30, 0, DateTimeKind.Utc)));
        Assert.False(MaintenanceSchedule.IsActive(w, new DateTime(2026, 11, 22, 21, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 28, 21, 0, 0, DateTimeKind.Utc), MaintenanceSchedule.NextStart(w, new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Contains("Sun", MaintenanceSchedule.Describe(w));
    }

    [Fact]
    public void Maintenance_ValidationCatchesBadInput()
    {
        Assert.NotNull(MaintenanceSchedule.Validate(Window(MaintenanceRecurrence.Once, x => { x.StartUtc = DateTime.UtcNow; x.EndUtc = DateTime.UtcNow.AddHours(-1); })));
        Assert.NotNull(MaintenanceSchedule.Validate(Window(MaintenanceRecurrence.Daily, x => { x.StartTimeLocal = "25:00"; x.DurationMin = 30; })));
        Assert.NotNull(MaintenanceSchedule.Validate(Window(MaintenanceRecurrence.Weekly, x => { x.StartTimeLocal = "02:00"; x.DurationMin = 30; })));
        Assert.NotNull(MaintenanceSchedule.Validate(Window(MaintenanceRecurrence.Cron, x => { x.CronExpression = "not cron"; x.DurationMin = 30; })));
        Assert.Null(MaintenanceSchedule.Validate(Window(MaintenanceRecurrence.Cron, x => { x.CronExpression = "0 2 1-7 * 0"; x.DurationMin = 240; })));
    }

    // ---------------------------------------------------------------- LDAP

    [Fact]
    public void Ldap_UsernameNormalisationAndFilterEscaping()
    {
        Assert.Equal("jdoe", LdapAuthenticator.NormalizeUsername(@"CORP\jdoe"));
        Assert.Equal("jdoe", LdapAuthenticator.NormalizeUsername("jdoe@corp.local"));
        Assert.Null(LdapAuthenticator.NormalizeUsername("j*doe"));
        Assert.Null(LdapAuthenticator.NormalizeUsername("a)(objectClass=*"));
        Assert.Equal(@"CN=Admins \28NW\29\2a,DC=corp", LdapAuthenticator.EscapeFilter("CN=Admins (NW)*,DC=corp"));
    }

    [Fact]
    public void Ldap_HighestMappedRoleWins()
    {
        var mappings = new[]
        {
            new LdapRoleMapping { GroupDn = "CN=NW Viewers,DC=corp", Role = UserRole.Viewer },
            new LdapRoleMapping { GroupDn = "CN=NW Admins,DC=corp", Role = UserRole.Admin },
            new LdapRoleMapping { GroupDn = "CN=NW Operators,DC=corp", Role = UserRole.Operator },
        };
        Assert.Equal(UserRole.Admin, LdapAuthenticator.ResolveRole(mappings, ["cn=nw admins,dc=corp", "CN=NW Viewers,DC=corp"]));
        Assert.Equal(UserRole.Operator, LdapAuthenticator.ResolveRole(mappings, ["CN=NW Operators,DC=corp"]));
        Assert.Null(LdapAuthenticator.ResolveRole(mappings, ["CN=Other,DC=corp"]));
    }

    [Fact]
    public void Ldap_SettingsRefuseCleartextPasswords()
    {
        var s = new LdapSettings
        {
            Enabled = true,
            Servers = ["dc01"],
            Domain = "corp.local",
            BaseDn = "DC=corp,DC=local",
            Security = LdapSecurity.None,
            BindMethod = LdapBindMethod.Simple,
            RoleMappings = [new LdapRoleMapping { GroupDn = "CN=x", Role = UserRole.Viewer }],
        };
        Assert.NotNull(s.Validate());
        s.BindMethod = LdapBindMethod.Negotiate;
        Assert.Null(s.Validate());
    }
}
