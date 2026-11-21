using System.Xml.Linq;
using NetWatch.Collectors.Servers;
using NetWatch.Collectors.Vmware;
using NetWatch.Core;
using NetWatch.Core.Credentials;

namespace NetWatch.Tests;

/// <summary>vim25 response parsing (synthetic responses in the vSphere Web Services format).</summary>
public class Phase3VmwareTests
{
    private const string Ns = "xmlns=\"urn:vim25\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"";

    private static IEnumerable<XElement> Objects(string inner) =>
        XElement.Parse($"<returnval {Ns}>{inner}</returnval>").Elements(Vim.Ns + "objects");

    private const string Inventory = """
        <objects><obj type="ClusterComputeResource">domain-c8</obj><propSet><name>name</name><val xsi:type="xsd:string">Prod-Cluster</val></propSet></objects>
        <objects><obj type="HostSystem">host-10</obj>
          <propSet><name>name</name><val xsi:type="xsd:string">esx01.corp.local</val></propSet>
          <propSet><name>parent</name><val type="ClusterComputeResource" xsi:type="ManagedObjectReference">domain-c8</val></propSet>
          <propSet><name>runtime.connectionState</name><val xsi:type="HostSystemConnectionState">connected</val></propSet>
          <propSet><name>runtime.inMaintenanceMode</name><val xsi:type="xsd:boolean">false</val></propSet>
          <propSet><name>overallStatus</name><val xsi:type="ManagedEntityStatus">yellow</val></propSet>
          <propSet><name>summary.hardware.cpuMhz</name><val xsi:type="xsd:int">2000</val></propSet>
          <propSet><name>summary.hardware.numCpuCores</name><val xsi:type="xsd:short">10</val></propSet>
          <propSet><name>summary.hardware.memorySize</name><val xsi:type="xsd:long">107374182400</val></propSet>
          <propSet><name>summary.quickStats.overallCpuUsage</name><val xsi:type="xsd:int">5000</val></propSet>
          <propSet><name>summary.quickStats.overallMemoryUsage</name><val xsi:type="xsd:int">51200</val></propSet>
          <propSet><name>runtime.healthSystemRuntime.systemHealthInfo.numericSensorInfo</name><val xsi:type="ArrayOfHostNumericSensorInfo">
            <HostNumericSensorInfo xsi:type="HostNumericSensorInfo"><name>Fan 1</name><healthState><label>Red</label><key>red</key></healthState></HostNumericSensorInfo>
            <HostNumericSensorInfo xsi:type="HostNumericSensorInfo"><name>PSU 1</name><healthState><label>Green</label><key>green</key></healthState></HostNumericSensorInfo>
          </val></propSet>
        </objects>
        <objects><obj type="VirtualMachine">vm-42</obj>
          <propSet><name>name</name><val xsi:type="xsd:string">APP01</val></propSet>
          <propSet><name>runtime.host</name><val type="HostSystem" xsi:type="ManagedObjectReference">host-10</val></propSet>
          <propSet><name>runtime.powerState</name><val xsi:type="VirtualMachinePowerState">poweredOn</val></propSet>
          <propSet><name>runtime.maxCpuUsage</name><val xsi:type="xsd:int">4000</val></propSet>
          <propSet><name>summary.quickStats.overallCpuUsage</name><val xsi:type="xsd:int">1000</val></propSet>
          <propSet><name>summary.config.numCpu</name><val xsi:type="xsd:int">2</val></propSet>
          <propSet><name>summary.config.memorySizeMB</name><val xsi:type="xsd:int">8192</val></propSet>
          <propSet><name>summary.quickStats.guestMemoryUsage</name><val xsi:type="xsd:int">2048</val></propSet>
          <propSet><name>summary.config.template</name><val xsi:type="xsd:boolean">false</val></propSet>
          <propSet><name>guest.ipAddress</name><val xsi:type="xsd:string">10.1.2.3</val></propSet>
          <propSet><name>guest.toolsRunningStatus</name><val xsi:type="xsd:string">guestToolsRunning</val></propSet>
          <propSet><name>guest.disk</name><val xsi:type="ArrayOfGuestDiskInfo">
            <GuestDiskInfo xsi:type="GuestDiskInfo"><diskPath>C:\</diskPath><capacity>100</capacity><freeSpace>20</freeSpace></GuestDiskInfo>
            <GuestDiskInfo xsi:type="GuestDiskInfo"><diskPath>D:\</diskPath><capacity>100</capacity><freeSpace>50</freeSpace></GuestDiskInfo>
          </val></propSet>
          <propSet><name>datastore</name><val xsi:type="ArrayOfManagedObjectReference"><ManagedObjectReference type="Datastore" xsi:type="ManagedObjectReference">datastore-5</ManagedObjectReference></val></propSet>
          <propSet><name>snapshot</name><val xsi:type="VirtualMachineSnapshotInfo">
            <currentSnapshot type="VirtualMachineSnapshot">snapshot-2</currentSnapshot>
            <rootSnapshotList><snapshot type="VirtualMachineSnapshot">snapshot-1</snapshot><name>base</name><createTime>2026-11-01T08:00:00Z</createTime>
              <childSnapshotList><snapshot type="VirtualMachineSnapshot">snapshot-2</snapshot><name>pre-patch</name><description>KB</description><createTime>2026-11-15T08:00:00Z</createTime></childSnapshotList>
            </rootSnapshotList>
          </val></propSet>
          <propSet><name>layoutEx.file</name><val xsi:type="ArrayOfVirtualMachineFileLayoutExFileInfo">
            <VirtualMachineFileLayoutExFileInfo><key>1</key><name>APP01.vmdk</name><size>1000</size></VirtualMachineFileLayoutExFileInfo>
            <VirtualMachineFileLayoutExFileInfo><key>2</key><name>APP01-000001.vmdk</name><size>300</size></VirtualMachineFileLayoutExFileInfo>
            <VirtualMachineFileLayoutExFileInfo><key>3</key><name>APP01-000002.vmdk</name><size>50</size></VirtualMachineFileLayoutExFileInfo>
            <VirtualMachineFileLayoutExFileInfo><key>4</key><name>APP01-Snapshot1.vmsn</name><size>7</size></VirtualMachineFileLayoutExFileInfo>
            <VirtualMachineFileLayoutExFileInfo><key>5</key><name>APP01-Snapshot2.vmsn</name><size>9</size></VirtualMachineFileLayoutExFileInfo>
          </val></propSet>
          <propSet><name>layoutEx.snapshot</name><val xsi:type="ArrayOfVirtualMachineFileLayoutExSnapshotLayout">
            <VirtualMachineFileLayoutExSnapshotLayout><key type="VirtualMachineSnapshot">snapshot-1</key><dataKey>4</dataKey><memoryKey>-1</memoryKey>
              <disk><key>2000</key><chain><fileKey>1</fileKey></chain></disk></VirtualMachineFileLayoutExSnapshotLayout>
            <VirtualMachineFileLayoutExSnapshotLayout><key type="VirtualMachineSnapshot">snapshot-2</key><dataKey>5</dataKey><memoryKey>-1</memoryKey>
              <disk><key>2000</key><chain><fileKey>1</fileKey></chain><chain><fileKey>2</fileKey></chain></disk></VirtualMachineFileLayoutExSnapshotLayout>
          </val></propSet>
          <propSet><name>layoutEx.disk</name><val xsi:type="ArrayOfVirtualMachineFileLayoutExDiskLayout">
            <VirtualMachineFileLayoutExDiskLayout><key>2000</key><chain><fileKey>1</fileKey></chain><chain><fileKey>2</fileKey></chain><chain><fileKey>3</fileKey></chain></VirtualMachineFileLayoutExDiskLayout>
          </val></propSet>
        </objects>
        <objects><obj type="VirtualMachine">vm-99</obj>
          <propSet><name>name</name><val xsi:type="xsd:string">TEMPLATE-W2022</val></propSet>
          <propSet><name>summary.config.template</name><val xsi:type="xsd:boolean">true</val></propSet>
        </objects>
        <objects><obj type="Datastore">datastore-5</obj>
          <propSet><name>summary.name</name><val xsi:type="xsd:string">DS01</val></propSet>
          <propSet><name>summary.capacity</name><val xsi:type="xsd:long">1000</val></propSet>
          <propSet><name>summary.freeSpace</name><val xsi:type="xsd:long">130</val></propSet>
          <propSet><name>summary.accessible</name><val xsi:type="xsd:boolean">true</val></propSet>
          <propSet><name>vm</name><val xsi:type="ArrayOfManagedObjectReference"><ManagedObjectReference type="VirtualMachine">vm-42</ManagedObjectReference></val></propSet>
        </objects>
        """;

    [Fact]
    public void Inventory_HostsVmsDatastoresAreParsed()
    {
        var data = new VmwarePollData();
        VimParser.ParseInventory(Objects(Inventory), data);

        var host = Assert.Single(data.Hosts);
        Assert.Equal("esx01.corp.local", host.Name);
        Assert.Equal("Prod-Cluster", host.Cluster);
        Assert.Equal(25, host.CpuPct);   // 5000 MHz of 10 x 2000 MHz
        Assert.Equal(50, host.MemPct);   // 50 GB of 100 GB
        Assert.Equal(1, host.SensorAlarms);
        Assert.Contains("Fan 1 (red)", host.SensorProblems);

        var vm = Assert.Single(data.Vms); // the template is skipped
        Assert.Equal("APP01", vm.Name);
        Assert.Equal("host-10", vm.HostMoRef);
        Assert.Equal(25, vm.CpuPct);
        Assert.Equal(25, vm.MemPct);
        Assert.Equal(80, vm.GuestDiskMaxPct);
        Assert.Equal(["datastore-5"], vm.DatastoreMoRefs);

        var ds = Assert.Single(data.Datastores);
        Assert.Equal("DS01", ds.Name);
        Assert.Equal(87, ds.UsedPct);
        Assert.Equal(1, ds.VmCount);
    }

    [Fact]
    public void Snapshots_TreeAndApproximateSizes()
    {
        var data = new VmwarePollData();
        VimParser.ParseInventory(Objects(Inventory), data);

        Assert.Equal(2, data.Snapshots.Count);
        var root = data.Snapshots.Single(s => s.MoRef == "snapshot-1");
        var child = data.Snapshots.Single(s => s.MoRef == "snapshot-2");
        Assert.Equal("base", root.Name);
        Assert.False(root.IsCurrent);
        Assert.True(child.IsCurrent);
        Assert.Equal(new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc), child.CreatedUtc);

        // snapshot-1 owns its state file (7) + the delta written after it (file 2: 300).
        Assert.Equal(307, root.SizeBytes);

        // snapshot-2 (current) owns its state file (9) + the running delta (file 3: 50).
        Assert.Equal(59, child.SizeBytes);
    }

    [Fact]
    public void Perf_CpuReadyFromSummation()
    {
        var counters = Objects("""
            <objects><obj type="PerformanceManager">PerfMgr</obj><propSet><name>perfCounter</name><val xsi:type="ArrayOfPerfCounterInfo">
              <PerfCounterInfo><key>2</key><nameInfo><key>usage</key></nameInfo><groupInfo><key>cpu</key></groupInfo><rollupType>average</rollupType></PerfCounterInfo>
              <PerfCounterInfo><key>12</key><nameInfo><key>ready</key></nameInfo><groupInfo><key>cpu</key></groupInfo><rollupType>summation</rollupType></PerfCounterInfo>
            </val></propSet></objects>
            """);
        Assert.Equal(12, VimParser.FindCounter(counters, "cpu", "ready", "summation"));

        var response = XElement.Parse($"""
            <QueryPerfResponse {Ns}><returnval xsi:type="PerfEntityMetric"><entity type="VirtualMachine">vm-42</entity>
              <value xsi:type="PerfMetricIntSeries"><id><counterId>12</counterId><instance></instance></id><value>1200</value><value>2000</value></value>
            </returnval></QueryPerfResponse>
            """);
        var (moRef, value) = Assert.Single(VimParser.ParsePerfSingleValues(response));
        Assert.Equal("vm-42", moRef);
        Assert.Equal(2000, value);

        // 2000 ms ready in a 20 s interval = 10 %, over 2 vCPUs = 5 % per vCPU.
        Assert.Equal(5, VimParser.ReadyPercent(2000, 20, 2));
    }

    [Fact]
    public void Alarms_OnlyRedAndYellowStates()
    {
        var folder = Objects("""
            <objects><obj type="Folder">group-d1</obj><propSet><name>triggeredAlarmState</name><val xsi:type="ArrayOfAlarmState">
              <AlarmState><key>alarm-7.datastore-5</key><entity type="Datastore">datastore-5</entity><alarm type="Alarm">alarm-7</alarm><overallStatus>yellow</overallStatus><time>2026-11-20T10:00:00Z</time><acknowledged>false</acknowledged></AlarmState>
              <AlarmState><key>alarm-1.host-10</key><entity type="HostSystem">host-10</entity><alarm type="Alarm">alarm-1</alarm><overallStatus>green</overallStatus><time>2026-11-20T10:00:00Z</time></AlarmState>
            </val></propSet></objects>
            """);
        var state = Assert.Single(VimParser.ParseAlarmStates(folder));
        Assert.Equal("alarm-7.datastore-5", state.Key);
        Assert.Equal("Datastore", state.Entity.Type);
        Assert.Equal("yellow", state.Status);
    }

    [Fact]
    public void Events_TypeSeverityAndEntity()
    {
        var lost = VimParser.ParseEvent(XElement.Parse($"""
            <returnval {Ns} xsi:type="HostConnectionLostEvent"><key>9001</key><createdTime>2026-11-20T10:00:00Z</createdTime>
              <host><host type="HostSystem">host-10</host><name>esx01.corp.local</name></host><fullFormattedMessage>Host esx01 is not responding</fullFormattedMessage></returnval>
            """))!;
        Assert.Equal(9001, lost.Key);
        Assert.Equal("HostConnectionLostEvent", lost.EventType);
        Assert.Equal(Severity.Major, lost.Severity);
        Assert.Equal("esx01.corp.local", lost.EntityName);

        var ex = VimParser.ParseEvent(XElement.Parse($"""
            <returnval {Ns} xsi:type="EventEx"><key>9002</key><createdTime>2026-11-20T10:01:00Z</createdTime><userName>VSPHERE.LOCAL\admin</userName>
              <vm><vm type="VirtualMachine">vm-42</vm><name>APP01</name></vm><fullFormattedMessage>Backup failed</fullFormattedMessage>
              <eventTypeId>com.vmware.vc.backup.failed</eventTypeId><severity>error</severity></returnval>
            """))!;
        Assert.Equal("com.vmware.vc.backup.failed", ex.EventType);
        Assert.Equal(Severity.Major, ex.Severity);
        Assert.Equal("APP01", ex.EntityName);
        Assert.Equal(@"VSPHERE.LOCAL\admin", ex.UserName);
    }

    [Fact]
    public void Login_UserNameGetsDomainSuffix()
    {
        ManagementCredential C(string user, string? domain) => new(CredentialKind.VCenter, new CredentialSecret { Username = user, Domain = domain, Password = "x" });
        Assert.Equal("ro@vsphere.local", VmwareCollector.UserName(C("ro", "vsphere.local")));
        Assert.Equal(@"CORP\ro", VmwareCollector.UserName(C(@"CORP\ro", "vsphere.local")));
        Assert.Equal("ro@corp.local", VmwareCollector.UserName(C("ro@corp.local", null)));
    }

    [Fact]
    public void MoRef_RoundTrip()
    {
        var m = new MoRef("VirtualMachine", "vm-42");
        var e = m.ToElement("obj");
        Assert.Equal("VirtualMachine", (string?)e.Attribute("type"));
        Assert.Equal(m, MoRef.From(e));
    }
}
