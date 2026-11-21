# NetWatch administration guide

All times in the console are shown in Pakistan Standard Time (Asia/Karachi). They are stored in UTC.

## 1. Roles

| Capability | Viewer | Operator | Admin |
|---|:-:|:-:|:-:|
| View overview, devices, switches, graphs, alerts, events | ✔ | ✔ | ✔ |
| Acknowledge, comment on and close alerts; *Poll now* | | ✔ | ✔ |
| Add/edit/delete devices, groups, credentials; import; discovery; maintenance mode and windows; interface settings; watched services | | | ✔ |
| Alert rules, notifications, users, directory (AD) sign-in, settings, audit trail, system status | | | ✔ |

Every change made by a user, and every sign-in (including failed ones), is recorded in *Settings → Audit*.

**Account security.**

- After 5 failed sign-ins an account is locked for 15 minutes. An Admin can unlock it earlier in *Settings →
  Users*.
- Sessions end after 30 minutes without activity and always after 12 hours.
- Users who get a password from an Admin must change it at their next sign-in.

All of these limits can be changed in *Settings → General → Security*.

**Active Directory sign-in** (*Settings → Directory (AD)*): users sign in with `user`, `DOMAIN\user` or
`user@domain`. NetWatch binds to a domain controller as that user (LDAPS recommended), so no directory service
account is stored. The role comes from the mapped AD groups (nested membership counts, the highest role wins) and is
re-read at every sign-in; users in no mapped group are refused. Directory users appear in *Settings → Users* with an
"AD" tag: an Admin can disable them there, but their password is managed in AD. Local accounts (e.g. `admin`) keep
working when the directory is unreachable. Use *Test sign-in* before saving.

## 2. Credentials

Create credentials first: *Settings → Credentials → Add*.

- **SNMP v2c:** the read-only community string.
- **SNMP v3:** the user name, security level, authentication protocol and password (SHA-256 or better
  recommended), and privacy protocol and password (AES recommended). The engine ID is discovered automatically.
  The same users are used to decrypt SNMPv3 traps.
- **Windows / WinRM:** a domain account with the rights listed in the installation guide (section 5). Used for
  WMI (WinRM, falling back to DCOM) and for reading event logs.
- **SSH (Linux):** user name and password and/or private key (OpenSSH/PEM; the password unlocks an encrypted key).

Use **Test** with an IP address to check a credential before assigning it; it shows the device's name,
description and object ID. Credentials are stored encrypted (Windows DPAPI). Once saved, a secret is never
shown again, only a hint such as `v3 nms / SHA256 / AES128`.

## 3. Devices

**Add one device:** *Devices → Add device*. Give it a name, IP, type, group and SNMP credential.

- Poll interval: 15–3600 s, default 60.
- *Parent* records the upstream device. In Phase 1 it is informational only; dependency-based alert suppression
  comes in Phase 2.

**Import many:** *Devices → Import* accepts a CSV file (comma or semicolon separated, UTF-8). Download the
template from the dialog. Columns:

| Column | Required | Notes |
|---|---|---|
| `name` | yes | Unique device name |
| `ip` | yes | IPv4 or IPv6 address |
| `type` | | Switch, Router, Firewall, Server, VM, DomainController (or `DC`), Hypervisor (`ESXi`, `Host`), VCenter, Other. Default Switch |
| `location` | | Free text |
| `group` | | Path like `HQ/Building A/Floor 1`; missing groups are created |
| `tags` | | Separated by `;` |
| `snmp_credential` | | Name of an existing credential |
| `poll_interval` | | Seconds, 15–3600 |
| `parent` | | Name or IP of the upstream device (may be another row of the same file) |
| `notes` | | Free text |

Rows with errors are listed with their line number and skipped; the valid rows are imported.

**Discover a subnet:** *Devices → Discover*. Enter a range in CIDR notation (largest allowed: /22, i.e. 1,022
addresses) and optionally an SNMP credential. NetWatch pings every address and queries the ones that answer.
It suggests the vendor, model and type from the SNMP object ID. Tick the devices to add.

**Switch interfaces** are found automatically on the first SNMP poll and refreshed every 30 minutes. On the
device page:

- Mark uplinks and server ports **Important**. Only important ports raise *port down* and *high utilisation*
  alerts.
- Untick **Monitored** for ports you don't need graphs for; this saves disk space.

**Maintenance mode** (one device, or several from the device list): no new alerts are raised for the devices
until the end time you choose. Polling and graphs continue, and existing alerts still resolve normally.

**Maintenance windows** (*Settings → Maintenance windows*) do the same on a schedule: one-off (start/end), daily,
weekly on selected days, or a cron expression, always in Pakistan time, for devices selected by device, group,
type and/or tag. The device page shows which window is active and until when.

**Servers** (*Servers* page, and the *Server* tab of a device): give servers, VMs and domain controllers a
*Windows / SSH credential* (device edit dialog) and NetWatch collects every poll, without agents:

| Data | Windows (WinRM/WMI) | Linux (SSH) |
|---|---|---|
| CPU, memory, uptime / last boot | ✔ | ✔ |
| Disk volumes (used %, free) | ✔ local fixed disks | ✔ mounted file systems |
| Network throughput (all adapters) | ✔ | ✔ |
| Watched services | Windows services | systemd units |
| Pending reboot | ✔ (servicing / Windows Update) | ✔ (`/var/run/reboot-required`) |
| Last patch | ✔ (latest hotfix) | — |
| Load average | — | ✔ (alert per CPU) |
| Event logs | System + Application, Critical/Error | use syslog |

Servers without such a credential but with SNMP still report CPU, memory and disks (HOST-RESOURCES-MIB).
*Watched services* are entered as service names (Windows: `W3SVC`, `MSSQLSERVER`, not the display name; Linux:
`sshd`, `nginx`); an alert is raised when one is not running. If a Linux server's SSH host key changes (e.g. after
a reinstall), collection stops with "host key changed" until an Admin clicks *Reset* next to the key on the
*Server* tab.

Windows event logs are read incrementally (bookmark per log); the first contact starts at the newest entry, so no
history flood. *Events & logs → Windows event logs* lists them with filters.

**Dependencies:** set *Upstream device* (the switch a device connects through). When an upstream device is
unreachable, alerts of the devices behind it are suppressed (they show "behind <switch>" in the device list); if a
device is still down after its upstream device came back, its alert is raised then.

**Device status:**

| Status | Meaning |
|---|---|
| Up | Answers ping |
| Warning | Answers ping, but SNMP fails or packet loss is 20 % or more |
| Down | No ping reply for 3 polls in a row (configurable) |
| Unknown | Not polled yet |
| Maintenance | Maintenance mode is on |

## 4. Alerts

The alert banner and the *Alerts* page update live.

- **New alert:** every new or resolved alert shows a pop-up in the corner. Critical pop-ups stay 60 s, others
  20 s.
- **Sound:** switch it on with the **bell icon** in the header; a crossed-out bell means it is off. Browsers only
  allow sound after a click, so it starts off. The choice is remembered in that browser. Once on, every new
  alert of severity Warning or higher plays a tone, with a different tone per severity.
- **Lifecycle:** **Active → Acknowledged → Resolved → Closed.**
  - An Operator acknowledges an alert (optionally with a comment) to show that someone is working on it.
  - Alerts resolve by themselves when the condition clears. Thresholds use hysteresis, e.g. CPU must drop
    below 85 % to clear a 90 % alert.
  - *Closed* removes an alert from the working list.
  - Event alerts such as *device rebooted* never resolve by themselves; close them when handled.
- **Deduplication:** a condition that persists produces one alert whose *occurrences* counter goes up, not a
  flood of alerts.
- **Flapping:** when an alert is raised and resolved 5 times within 30 minutes it is marked *flapping* and stays
  open until the condition has been clear for 15 minutes (no notification storm).
- **Escalation:** a Critical alert not acknowledged within 15 minutes is marked *escalated*: a pop-up appears, the
  alarm repeats every 5 minutes in every browser with sound enabled, and the notification channels are notified
  again. Times are set in *Settings → General → Alert lifecycle* (Major can escalate too); a rule can override
  them.
- **Suppression:** maintenance and unreachable upstream devices prevent new alerts (see section 3).

**Built-in rules** (*Settings → Alert rules*; edit thresholds, severity or scope, or disable them):

| Rule | Condition | Severity |
|---|---|---|
| Device down | No ping reply (see status) | Critical |
| High latency | Ping RTT > 100 ms for 5 min (clears < 80 ms) | Warning |
| Packet loss | Loss > 20 % for 3 min | Warning |
| High CPU | CPU > 90 % for 5 min (clears < 85 %) | Major |
| High memory | Memory > 90 % for 5 min (clears < 85 %) | Major |
| Important port down | Operational status down on an important interface | Major |
| High interface utilisation | > 90 % in either direction on an important interface | Warning |
| Hardware sensor problem | Fan, power supply or temperature sensor not OK | Major |
| SNMP not responding | Device answers ping but not SNMP for 3 min | Warning |
| Device rebooted | sysUpTime went backwards | Warning |
| Disk space low / critical | Volume more than 85 % / 95 % used | Warning / Critical |
| Service not running | A watched service is not running for 1 min | Major |
| Server monitoring failing | Ping works but WinRM/WMI/SSH fails for 5 min | Warning |
| High load (Linux) | 5-min load average > 2 per CPU for 10 min | Warning |
| Windows critical event | Critical entry in System/Application log | Major |
| Syslog emergency/alert/critical | Syslog severity 0–2 | Major |
| SNMP authentication failure | authenticationFailure trap | Warning |
| Repeated login failures (syslog) | ≥ 10 failed-login messages from one device in 5 min | Warning |

You can add your own rules:

| Type | Example |
|---|---|
| Threshold | Disk free < 10 GB for 10 min |
| State change | Interface down, service not running |
| Event | Syslog message matching `%SYS-5-CONFIG`; trap OID `1.3.6.1.4.1.9.9.43.2.0.1`; Windows event ID 7031 |
| Count in window | ≥ 20 syslog messages matching `denied` in 5 min |
| Absence | No syslog from a firewall for 30 min; no CPU data for 15 min |

- **Scope:** a rule can apply to specific devices, groups, device types or tags.
- **Duration:** how long a condition must last before the alert is raised.
- **Event filters:** message pattern (regular expression), minimum severity, source (app/provider/trap name),
  Windows event IDs, trap OID prefix.
- **Absence** watches sources that have delivered data since the service started; a restart begins a new period.

**Notifications** (*Settings → Notifications*) besides the dashboard, all offline:

- **Windows Event Log:** Application log, source "NetWatch" (event IDs 1000+ raised, 2000 resolved, 3000+
  escalated) — for existing event-log collection.
- **Syslog forwarding:** RFC 5424 over UDP or TCP to a SIEM.
- **Script:** a `.ps1`, `.cmd`, `.bat` or `.exe` placed in `C:\ProgramData\NetWatch\scripts` (only that folder is
  allowed). Alert data arrives in `NETWATCH_*` environment variables and as JSON on standard input.
- **E-mail:** through an internal SMTP relay; 5 or more notifications at once are combined into one digest.

Each channel has a minimum severity, a *Send test* button and a delivery status.

## 4b. VMware (vCenter / ESXi)

Add the vCenter as a device of type **vCenter** (or a standalone ESXi host as type **Hypervisor**), create a
*vCenter* credential (*Settings → Credentials*, an account with the Read-only role, see the installation guide)
and select it as *vCenter / ESXi credential* in the device dialog. *Test* on the credential page shows what the
account can see. Every poll (60 s by default) NetWatch reads through the vSphere API:

| Object | Collected |
|---|---|
| ESXi hosts | Connection state, maintenance mode, CPU and memory use, hardware sensors, model, ESXi version, uptime, cluster |
| VMs | Power state, host, CPU %, **CPU ready** (per vCPU), memory, ballooning, VMware Tools state, guest OS and IP, guest disk fill, datastores, snapshots |
| Datastores | Capacity, free space, provisioned (thin) space, accessibility |
| Snapshots | Every snapshot with age and approximate size (state file plus delta disks written since it) |
| vCenter alarms | Triggered (red/yellow) alarms that are not acknowledged in vCenter |
| vCenter events | New events from the first poll on (no history import): host connection lost, HA failover, VM powered off, DRS migrations … |

The **VMware** page shows the hosts with their VMs, VM, datastore and snapshot reports, alarms and events. VMs and
hosts that are also NetWatch devices (same IP or name, e.g. a VM monitored by ping/WinRM) are linked, and their
VMware alerts (snapshots, CPU ready, Tools) appear on that device; the device page shows a VMware card.

Built-in VMware rules: *vCenter/ESXi API not reachable*, *ESXi host not connected* (Critical), *ESXi host hardware
alarm*, *ESXi host high CPU/memory* (> 90 %), *Datastore space low/critical* (> 85 % / 95 %), *Datastore not
accessible*, *Old snapshot* (> 7 days), *Large snapshot* (> 50 GB), *VM CPU ready high* (> 10 % for 10 min),
*VMware Tools not running* (15 min), *vCenter alarm (red/yellow)*, *vCenter major event*. Adjust them in
*Settings → Alert rules*. A deleted snapshot or a cleared vCenter alarm resolves its alert at the next poll.

**Certificates:** vCenter normally uses a certificate from its own CA (VMCA). If that CA is not trusted on the
NetWatch server, the certificate's SHA-256 thumbprint is pinned at the first connection (*Settings → General →
VMware*, on by default) and a different certificate is refused later. After a planned certificate renewal click
*Reset* next to the pinned certificate on the vCenter's device page.

## 4a. Syslog and SNMP traps

Point devices at the NetWatch server: syslog to UDP or TCP 514, SNMP traps/informs to UDP 162. Messages are matched
to devices by their source address (for relayed SNMPv1 traps, by the agent address). *Events & logs* shows them with
filters (device, severity, text, time). linkUp/linkDown and cold/warm start traps trigger an immediate poll of the
device. Messages from unknown addresses are kept without a device (configurable) and raise no alerts. A rate limit
(default 2,000 messages/s) protects the database during floods; *Settings → System status* shows received, stored and
dropped counts and whether the listeners are running.

Example (Cisco IOS):

```
logging host <netwatch-ip> transport udp port 514
snmp-server host <netwatch-ip> version 2c <community>
snmp-server enable traps snmp linkdown linkup coldstart warmstart
```

## 5. Settings

| Section | Defaults |
|---|---|
| Monitoring | Poll interval 60 s; Down after 3 failed polls; 3 pings, 1000 ms timeout; SNMP timeout 3000 ms, 1 retry; 64 parallel polls |
| Security | Idle timeout 30 min; absolute 12 h; lockout after 5 failures for 15 min; minimum password length 10 |
| Alert lifecycle | Flapping after 5 changes in 30 min, stable 15 min; escalate Critical after 15 min; repeat sound every 5 min; dependency suppression on |
| Server monitoring | WinRM 5985 (DCOM fallback on), event logs Critical/Error, max 200 entries per log and poll, SSH 22, timeout 20 s |
| VMware | Events and alarms imported, CPU ready collected, certificates pinned on first use, API timeout 60 s, max 500 events per poll |
| Receivers | Syslog UDP/TCP 514, traps UDP 162, unknown sources stored, 2,000 messages/s |
| Retention | Raw samples 7 days; 5-minute averages 90 days; hourly averages 730 days; events (device events, syslog, traps, Windows events), audit and closed alerts 365 days |

Retention runs every night at 02:30. Old data is removed in whole-day, month or year blocks, so it is fast and
does not fragment the database. Changing *parallel polls* takes effect after a service restart; all other
settings apply immediately.

*Settings → System* shows:

- the version and uptime;
- the polling rate and failures, and whether polls overrun their interval;
- the metric writer queue;
- the database sizes and free disk space.

## 6. Backup and restore

All state is in `C:\ProgramData\NetWatch`:

| Path | Content |
|---|---|
| `data\netwatch.db` | Configuration: devices, interfaces, credentials (encrypted), rules, alerts, users, settings, audit |
| `data\events.db` | Device events (reboots, status changes) |
| `data\metrics.db` | Performance history |
| `appsettings.json` | Port and certificate settings |
| `certs\` | TLS certificate (private key encrypted) |
| `logs\` | Service logs (30 days) |

**Backup:**

1. Stop the service: `Stop-Service NetWatch`.
2. Copy the whole folder, including any `*.db-wal` files, to your backup location.
3. Start the service again: `Start-Service NetWatch`.

`metrics.db` can be left out if you don't need the history; it is usually by far the largest file.

**Restore on the same server:**

1. Stop the service.
2. Replace the folder with the backup.
3. Start the service.

**Restore on a different server:**

1. Install NetWatch on the new server.
2. Stop the service.
3. Copy the backup over `C:\ProgramData\NetWatch`.
4. Delete the `certs\` folder; a new certificate is generated on start.
5. Start the service.
6. Re-enter the secret of every credential in *Settings → Credentials*. DPAPI ties encrypted secrets to the
   original server, so they cannot be decrypted on the new one. Devices, history, alerts and users carry over.

## 7. Troubleshooting

| Symptom | Check |
|---|---|
| Console does not open | `Get-Service NetWatch` is *Running*? Latest log in `C:\ProgramData\NetWatch\logs`. From an operator PC: `Test-NetConnection <server> -Port 8443` |
| Browser certificate warning | Expected with the self-signed certificate. See the installation guide, section 4 |
| Forgot the admin password | Elevated prompt: `"C:\Program Files\NetWatch\NetWatch.Service.exe" --reset-admin`, then `Restart-Service NetWatch` |
| Device shows *Warning* "SNMP not responding" | Test the credential on *Settings → Credentials → Test*. Check the device's SNMP ACL allows the NetWatch server, and that UDP 161 isn't blocked by a firewall in between. For v3 the Test button names the exact problem (unknown user, wrong authentication password, decryption error) |
| Device *Down* but reachable | Some firewalls/servers drop ICMP. Allow ICMP echo from the NetWatch server (on Windows: the *File and Printer Sharing (Echo Request – ICMPv4-In)* rule) |
| Interface graphs empty | Graphs need two polls to compute a rate; wait 2 poll intervals. Check that the interface is ticked *Monitored* |
| CPU/memory missing on a switch | The model doesn't expose them through the supported MIBs (Cisco PROCESS/MEMORY-POOL, HP/Aruba, Juniper jnxOperating, HOST-RESOURCES). Ping and interfaces still work |
| "Polls overrunning" on *System* | Too many devices for the poll interval. Increase *parallel polls* (then restart) or the poll interval |
| VMware: "vCenter certificate changed" | The vCenter certificate was renewed or the address now points elsewhere. Verify, then *Reset* the pinned certificate on the device page |
| VMware: "lacks permission" / empty inventory | Assign the Read-only role at the vCenter root with *propagate to children* |
| VMware: CPU ready missing | Statistics level 1 is enough for real-time counters; check that *Collect VM CPU ready* is on |
| Server tab: "WinRM … failing" | Check WinRM is enabled on the server (`Test-WSMan <server>` from the NetWatch server), the firewall rule *Windows Remote Management (HTTP-In)*, and that the account is in *Remote Management Users*. With DCOM fallback on, the error lists both attempts |
| "Access denied" on WMI | Grant *Remote Enable* on `root\cimv2` and `root\default`, add the account to *Distributed COM Users* |
| No Windows events, "Event logs: …" warning | Add the account to *Event Log Readers*; enable the firewall rule *Remote Event Log Management* |
| Linux: "SSH host key changed" | The server was reinstalled or its key changed; verify, then *Reset* the key on the Server tab |
| No syslog / traps arrive | *Settings → System status* must show the listeners as "listening" (another program may use port 514/162). Check the device sends to the right address and the firewall rules exist |
| AD sign-in: "directory cannot be reached" | Check *Domain controllers* and port (636 for LDAPS); the DC certificate must be trusted on the NetWatch server (or use StartTLS/Negotiate) |

Logs are plain text, one file per day, kept for 30 days. For more detail, set `"Logging": { "MinimumLevel":
"Debug" }` in `appsettings.json` and restart the service. Set it back afterwards, because debug logging is
verbose.
