namespace NetWatch.Collectors.Snmp;

/// <summary>
/// OIDs used by NetWatch. Vendor OIDs are hard-coded so no MIB files are needed on the air-gapped server.
/// </summary>
public static class Oids
{
    // SNMPv2-MIB system group
    public const string SysDescr = "1.3.6.1.2.1.1.1.0";
    public const string SysObjectId = "1.3.6.1.2.1.1.2.0";
    public const string SysUpTime = "1.3.6.1.2.1.1.3.0";
    public const string SysName = "1.3.6.1.2.1.1.5.0";
    public const string SysLocation = "1.3.6.1.2.1.1.6.0";

    // IF-MIB ifTable
    public const string IfDescr = "1.3.6.1.2.1.2.2.1.2";
    public const string IfType = "1.3.6.1.2.1.2.2.1.3";
    public const string IfSpeed = "1.3.6.1.2.1.2.2.1.5";
    public const string IfPhysAddress = "1.3.6.1.2.1.2.2.1.6";
    public const string IfAdminStatus = "1.3.6.1.2.1.2.2.1.7";
    public const string IfOperStatus = "1.3.6.1.2.1.2.2.1.8";
    public const string IfInOctets = "1.3.6.1.2.1.2.2.1.10";
    public const string IfInDiscards = "1.3.6.1.2.1.2.2.1.13";
    public const string IfInErrors = "1.3.6.1.2.1.2.2.1.14";
    public const string IfOutOctets = "1.3.6.1.2.1.2.2.1.16";
    public const string IfOutDiscards = "1.3.6.1.2.1.2.2.1.19";
    public const string IfOutErrors = "1.3.6.1.2.1.2.2.1.20";
    public const string IfLastChange = "1.3.6.1.2.1.2.2.1.9";

    // BRIDGE-MIB (RFC 4188): bridge port -> ifIndex, and the learned MAC table without VLANs
    public const string Dot1dBasePortIfIndex = "1.3.6.1.2.1.17.1.4.1.2";
    public const string Dot1dTpFdbPort = "1.3.6.1.2.1.17.4.3.1.2";
    public const string Dot1dTpFdbStatus = "1.3.6.1.2.1.17.4.3.1.3";

    // Q-BRIDGE-MIB (RFC 4363): learned MAC table per filtering database (VLAN), and VLAN -> FDB id
    public const string Dot1qTpFdbPort = "1.3.6.1.2.1.17.7.1.2.2.1.2";
    public const string Dot1qTpFdbStatus = "1.3.6.1.2.1.17.7.1.2.2.1.3";
    public const string Dot1qVlanFdbId = "1.3.6.1.2.1.17.7.1.4.2.1.3";

    // Configured (static) unicast addresses: Q-BRIDGE dot1qStaticUnicastTable and BRIDGE-MIB dot1dStaticTable (PortList bitmaps)
    public const string Dot1qStaticUnicastAllowedToGoTo = "1.3.6.1.2.1.17.7.1.3.1.1.3";
    public const string Dot1qStaticUnicastStatus = "1.3.6.1.2.1.17.7.1.3.1.1.4";
    public const string Dot1dStaticAllowedToGoTo = "1.3.6.1.2.1.17.5.1.1.3";
    public const string Dot1dStaticStatus = "1.3.6.1.2.1.17.5.1.1.4";

    // IP-MIB ARP table (ipNetToMediaTable): MAC -> IPv4 on routed switches
    public const string IpNetToMediaPhysAddress = "1.3.6.1.2.1.4.22.1.2";
    public const string IpNetToMediaType = "1.3.6.1.2.1.4.22.1.4";

    // TP-Link JetStream / Omada switches, TPLINK-SYSMONITOR-MIB (enterprise 11863): one row per unit of a stack
    public const string TpLinkCpu5Seconds = "1.3.6.1.4.1.11863.6.4.1.1.1.1.2";
    public const string TpLinkCpu1Minute = "1.3.6.1.4.1.11863.6.4.1.1.1.1.3";
    public const string TpLinkMemoryUtilization = "1.3.6.1.4.1.11863.6.4.1.2.1.1.2";

    // EtherLike-MIB
    public const string Dot3StatsDuplexStatus = "1.3.6.1.2.1.10.7.2.1.19";

    // LLDP-MIB (IEEE 802.1AB-2005, lldpRemTable / lldpLocPortTable / lldpRemManAddrTable)
    public const string LldpLocPortId = "1.0.8802.1.1.2.1.3.7.1.3";
    public const string LldpLocPortDesc = "1.0.8802.1.1.2.1.3.7.1.4";
    public const string LldpRemChassisIdSubtype = "1.0.8802.1.1.2.1.4.1.1.4";
    public const string LldpRemChassisId = "1.0.8802.1.1.2.1.4.1.1.5";
    public const string LldpRemPortIdSubtype = "1.0.8802.1.1.2.1.4.1.1.6";
    public const string LldpRemPortId = "1.0.8802.1.1.2.1.4.1.1.7";
    public const string LldpRemPortDesc = "1.0.8802.1.1.2.1.4.1.1.8";
    public const string LldpRemSysName = "1.0.8802.1.1.2.1.4.1.1.9";
    public const string LldpRemSysDesc = "1.0.8802.1.1.2.1.4.1.1.10";
    public const string LldpRemManAddrIfSubtype = "1.0.8802.1.1.2.1.4.2.1.3";

    // CISCO-CDP-MIB cdpCacheTable
    public const string CdpCacheAddressType = "1.3.6.1.4.1.9.9.23.1.2.1.1.3";
    public const string CdpCacheAddress = "1.3.6.1.4.1.9.9.23.1.2.1.1.4";
    public const string CdpCacheDeviceId = "1.3.6.1.4.1.9.9.23.1.2.1.1.6";
    public const string CdpCacheDevicePort = "1.3.6.1.4.1.9.9.23.1.2.1.1.7";
    public const string CdpCachePlatform = "1.3.6.1.4.1.9.9.23.1.2.1.1.8";

    // IF-MIB ifXTable
    public const string IfName = "1.3.6.1.2.1.31.1.1.1.1";
    public const string IfHcInOctets = "1.3.6.1.2.1.31.1.1.1.6";
    public const string IfHcOutOctets = "1.3.6.1.2.1.31.1.1.1.10";
    public const string IfHighSpeed = "1.3.6.1.2.1.31.1.1.1.15";
    public const string IfAlias = "1.3.6.1.2.1.31.1.1.1.18";

    // ENTITY-MIB entPhysicalTable
    public const string EntPhysicalDescr = "1.3.6.1.2.1.47.1.1.1.1.2";
    public const string EntPhysicalClass = "1.3.6.1.2.1.47.1.1.1.1.5";
    public const string EntPhysicalName = "1.3.6.1.2.1.47.1.1.1.1.7";
    public const string EntPhysicalFirmwareRev = "1.3.6.1.2.1.47.1.1.1.1.9";
    public const string EntPhysicalSoftwareRev = "1.3.6.1.2.1.47.1.1.1.1.10";
    public const string EntPhysicalSerialNum = "1.3.6.1.2.1.47.1.1.1.1.11";
    public const string EntPhysicalModelName = "1.3.6.1.2.1.47.1.1.1.1.13";

    // ENTITY-SENSOR-MIB entPhySensorTable
    public const string EntPhySensorType = "1.3.6.1.2.1.99.1.1.1.1";
    public const string EntPhySensorScale = "1.3.6.1.2.1.99.1.1.1.2";
    public const string EntPhySensorPrecision = "1.3.6.1.2.1.99.1.1.1.3";
    public const string EntPhySensorValue = "1.3.6.1.2.1.99.1.1.1.4";
    public const string EntPhySensorOperStatus = "1.3.6.1.2.1.99.1.1.1.5";

    // HOST-RESOURCES-MIB
    public const string HrProcessorLoad = "1.3.6.1.2.1.25.3.3.1.2";
    public const string HrStorageType = "1.3.6.1.2.1.25.2.3.1.2";
    public const string HrStorageAllocationUnits = "1.3.6.1.2.1.25.2.3.1.4";
    public const string HrStorageSize = "1.3.6.1.2.1.25.2.3.1.5";
    public const string HrStorageUsed = "1.3.6.1.2.1.25.2.3.1.6";
    public const string HrStorageRam = "1.3.6.1.2.1.25.2.1.2";

    // Cisco: CISCO-PROCESS-MIB, CISCO-MEMORY-POOL-MIB, CISCO-ENVMON-MIB
    public const string CiscoCpu5MinRev = "1.3.6.1.4.1.9.9.109.1.1.1.1.8";
    public const string CiscoCpuMemUsed = "1.3.6.1.4.1.9.9.109.1.1.1.1.12";
    public const string CiscoCpuMemFree = "1.3.6.1.4.1.9.9.109.1.1.1.1.13";
    public const string CiscoMemPoolUsed = "1.3.6.1.4.1.9.9.48.1.1.1.5";
    public const string CiscoMemPoolFree = "1.3.6.1.4.1.9.9.48.1.1.1.6";
    public const string CiscoEnvTempDescr = "1.3.6.1.4.1.9.9.13.1.3.1.2";
    public const string CiscoEnvTempValue = "1.3.6.1.4.1.9.9.13.1.3.1.3";
    public const string CiscoEnvTempState = "1.3.6.1.4.1.9.9.13.1.3.1.6";
    public const string CiscoEnvFanDescr = "1.3.6.1.4.1.9.9.13.1.4.1.2";
    public const string CiscoEnvFanState = "1.3.6.1.4.1.9.9.13.1.4.1.3";
    public const string CiscoEnvPsuDescr = "1.3.6.1.4.1.9.9.13.1.5.1.2";
    public const string CiscoEnvPsuState = "1.3.6.1.4.1.9.9.13.1.5.1.3";

    // HP / HPE ProCurve & ArubaOS-Switch: STATISTICS-MIB, NETSWITCH-MIB, HP-ICF-CHASSIS
    public const string HpCpu = "1.3.6.1.4.1.11.2.14.11.5.1.9.6.1.0";
    public const string HpMemTotal = "1.3.6.1.4.1.11.2.14.11.5.1.1.2.1.1.1.5";
    public const string HpMemFree = "1.3.6.1.4.1.11.2.14.11.5.1.1.2.1.1.1.6";
    public const string HpSensorStatus = "1.3.6.1.4.1.11.2.14.11.1.2.6.1.4";
    public const string HpSensorDescr = "1.3.6.1.4.1.11.2.14.11.1.2.6.1.7";

    // Juniper: JUNIPER-MIB jnxOperatingTable
    public const string JnxOperatingDescr = "1.3.6.1.4.1.2636.3.1.13.1.5";
    public const string JnxOperatingState = "1.3.6.1.4.1.2636.3.1.13.1.6";
    public const string JnxOperatingTemp = "1.3.6.1.4.1.2636.3.1.13.1.7";
    public const string JnxOperatingCpu = "1.3.6.1.4.1.2636.3.1.13.1.8";
    public const string JnxOperatingBuffer = "1.3.6.1.4.1.2636.3.1.13.1.11";
}

/// <summary>Vendor identification from sysObjectID enterprise numbers.</summary>
public static class VendorCatalog
{
    private static readonly (string Prefix, string Vendor)[] Enterprises =
    [
        ("1.3.6.1.4.1.9.", "Cisco"),
        ("1.3.6.1.4.1.11.", "HP"),
        ("1.3.6.1.4.1.47196.", "HPE Aruba"),
        ("1.3.6.1.4.1.14823.", "Aruba"),
        ("1.3.6.1.4.1.2636.", "Juniper"),
        ("1.3.6.1.4.1.11863.", "TP-Link"),
        ("1.3.6.1.4.1.2011.", "Huawei"),
        ("1.3.6.1.4.1.25506.", "H3C"),
        ("1.3.6.1.4.1.6486.", "Alcatel-Lucent"),
        ("1.3.6.1.4.1.12356.", "Fortinet"),
        ("1.3.6.1.4.1.25461.", "Palo Alto"),
        ("1.3.6.1.4.1.311.", "Microsoft"),
        ("1.3.6.1.4.1.8072.", "Net-SNMP"),
        ("1.3.6.1.4.1.6876.", "VMware"),
        ("1.3.6.1.4.1.674.", "Dell"),
        ("1.3.6.1.4.1.232.", "HPE"),
        ("1.3.6.1.4.1.2.", "IBM"),
    ];

    public static string? VendorFromSysObjectId(string? sysObjectId)
    {
        if (string.IsNullOrEmpty(sysObjectId))
        {
            return null;
        }

        var oid = sysObjectId.TrimStart('.') + ".";
        return Enterprises.FirstOrDefault(e => oid.StartsWith(e.Prefix, StringComparison.Ordinal)).Vendor;
    }

    /// <summary>Best guess of the device type for discovery results.</summary>
    public static Core.DeviceType SuggestType(string? vendor, string? sysDescr)
    {
        var d = sysDescr ?? string.Empty;
        bool Has(string s) => d.Contains(s, StringComparison.OrdinalIgnoreCase);
        return vendor switch
        {
            "Fortinet" or "Palo Alto" => Core.DeviceType.Firewall,
            "VMware" => Core.DeviceType.Hypervisor,
            "Microsoft" or "Net-SNMP" or "Dell" or "HPE" or "IBM" => Core.DeviceType.Server,
            _ when Has("firewall") || Has("Adaptive Security Appliance") => Core.DeviceType.Firewall,
            _ when Has(" router") || Has("ISR") || Has("ASR") || Has("SRX") || Has(" MX") => Core.DeviceType.Router,
            null => Has("Windows") || Has("Linux") ? Core.DeviceType.Server : Core.DeviceType.Other,
            _ => Core.DeviceType.Switch,
        };
    }
}
