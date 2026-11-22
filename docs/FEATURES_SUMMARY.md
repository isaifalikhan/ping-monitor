# NetWatch 2.0.0 — Feature summary for management

*Compiled 2026-11-22 from the source code and checked on a running instance (simulator mode). Full details:
[FEATURES.md](FEATURES.md) · spreadsheet: [FEATURES.csv](FEATURES.csv).*

## What NetWatch is

NetWatch is a monitoring system for the air-gapped network. One Windows service on one server watches the
switches, servers, VMware hosts and VMs, and Active Directory domain controllers. It raises alerts and shows
everything in a web console that operators open in their browser. It needs no agents on the monitored systems
and no internet access, and it brings no external software or cloud services with it.

## Key capabilities

**Switches and network devices**
- Up/down status, latency and packet loss for every device (ping), with SNMP v2c and v3 (SHA-2, AES-256).
- Every port: status, speed, duplex, traffic, utilization, errors and discards, with 1-hour to 30-day graphs and a live port grid.
- Health (CPU, memory, temperature, fans, power supplies) for Cisco, HP ProCurve/ArubaOS-Switch and Juniper. ArubaOS-CX uses a profile whose OIDs are not yet checked on a real switch. Other vendors use standard MIBs.
- Important ports (uplinks, server ports) alert on going down, half duplex, running below their expected speed, link flapping and high utilization.
- SNMP trap and syslog receivers. LLDP/CDP neighbours feed a topology map and parent-device suggestions.

**Servers**
- Windows without an agent (WinRM/WMI, DCOM fallback): CPU, memory, disks, network, services, System/Application event-log errors, pending reboot, last patch.
- Linux over SSH (CPU, memory, disks, load, systemd units). Servers that offer only SNMP report CPU, memory and disks.
- GPUs (NVIDIA, AMD, Intel) and server hardware health from HPE iLO / Dell iDRAC (Redfish), with an SNMP fallback.

**VMware**
- vCenter or standalone ESXi with a read-only account: hosts, VMs, datastores, snapshot age and size, CPU ready, VMware Tools, vCenter alarms and events.

**Domain controllers**
- 26 Security-log event types (logons, failures, lockouts, account and group changes, admin logons, audit-policy changes, log clearing, directory changes). They are read remotely or received by Windows Event Forwarding, and the two methods can be mixed per DC.
- Built-in security alerts: password guessing per account and per source, lockouts, additions to privileged groups, off-hours admin logons, log cleared, audit policy changed, accounts created or deleted, Kerberos failure spikes.
- Replication per partner, AD services, clock difference to the PDC, and LDAP/Kerberos/DNS port checks. A searchable event viewer with saved searches, a lockout tracker and a group-change log.

**Alerting**
- Five rule types (threshold, state, event, count-in-window, absence) and 61 ready-made rules.
- Alerts are acknowledged, commented and closed, and each keeps a history. The engine removes duplicates, detects flapping alerts, suppresses alerts behind a failed upstream device, honours maintenance windows and escalates.
- Notifications: on-screen pop-up and sound, e-mail through an internal relay, syslog to a SIEM, the Windows Event Log, and custom scripts.

**Dashboards, reports, operations**
- Overview, device, switch, server, VMware, DC, topology, event and alert pages. Also a full-screen NOC wall display with read-only kiosk links, and dark and light themes.
- Seven reports in PDF or CSV (availability, alerts, interface utilization, capacity with 30-day disk prediction, DC security, inventory, VMware). They can run on demand or on a daily, weekly or monthly schedule with e-mail.
- Automatic nightly backups that run while the service keeps working. A portable backup lets you move to a new server, and restores can be started from the console.
- Pilot tools: connection tests for 10 protocols, a diagnostic bundle, and a simulator for training without hardware.

## Scale it supports (measured)

| Tested | Result |
|---|---|
| 306 devices: 240 switches, 20 servers, 40 VMs, 2 DCs, vCenter + 4 hosts; ≈ 12,500 interfaces; 60-second polling | ≈ 320 polls per minute, average poll < 0.5 s |
| 1,000 DC security events/min (soak test: 4,000/min) | No messages dropped |
| Resource use | ≈ 2–2.5 % of one CPU core, ≈ 330 MB memory |
| Console response | Main pages’ data in ≤ 41 ms |

A server with **2 vCPU and 4 GB RAM** is sufficient at this scale. Plan the disk for the history. Metrics take
about 50 GB after two years for 12,500 interfaces. DC events take about 0.9 GB per day at 1,000 events per minute,
so the event retention period sets most of the disk need.

## Air-gap and security points

- **No internet at any time:** no cloud, no telemetry, no update checks, no web fonts or CDNs. The build fails if the UI references an external address. Certificate checks never try to go online.
- **Can be built inside the air gap** from a prepared package folder. Every package is pinned by a content hash, so a tampered package fails the build.
- **Read-only towards devices:** NetWatch never writes to switches, servers, vCenter or AD. Accounts need read rights only.
- **Protected secrets:** device credentials are encrypted with Windows DPAPI and are never shown again or written to logs. Portable backups use AES-256 with a password that is never stored.
- **Controlled access:** HTTPS only (TLS 1.2/1.3). Roles are Viewer, Operator and Admin, with optional AD sign-in by group. Accounts lock after failed sign-ins and sessions time out. Every change and sign-in is in the audit trail. Strict browser security headers apply.
- **Verified:** 304 of 304 automated tests pass. An executed permission check of 137 endpoints × 5 identities found no violation. No test secret was found in any database, log, backup or support bundle.

## Feature status

| Status | Count | Meaning |
|---|---|---|
| ✅ Available | **106** | Works and verified |
| 🟡 Limited | **9** | Works, with a stated limitation |
| 🧪 Pending pilot | **60** | Works in the simulator, still to be confirmed on the real devices |
| ⛔ Not available | **8** | Gaps, listed in FEATURES.md table G |
| **Total features listed** | **183** | (175 available in some form) |

Most of the 🧪 items are the device connections themselves: SNMP per vendor, WinRM, SSH, vSphere, Redfish, DC
event logs, AD sign-in and the SMTP relay. The installer is also 🧪. The release report's current gate is
**NO-GO** until:

1. The installer has been run on Windows Server 2022.
2. One open Medium bug (BUG-006: one poll overrun in about 147,000 polls) has been resolved.
3. The pilot has confirmed these items on real equipment.
