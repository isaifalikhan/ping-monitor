# NetWatch administration guide

All times in the console are shown in Pakistan Standard Time (Asia/Karachi). They are stored in UTC.

## 1. Roles

| Capability | Viewer | Operator | Admin |
|---|:-:|:-:|:-:|
| View overview, devices, switches, graphs, alerts, events | ✔ | ✔ | ✔ |
| Acknowledge, comment on and close alerts; *Poll now* | | ✔ | ✔ |
| Add/edit/delete devices, groups, credentials; import; discovery; maintenance mode; interface settings | | | ✔ |
| Alert rules, users, settings, audit trail, system status | | | ✔ |

Every change made by a user, and every sign-in (including failed ones), is recorded in *Settings → Audit*.

**Account security.**

- After 5 failed sign-ins an account is locked for 15 minutes. An Admin can unlock it earlier in *Settings →
  Users*.
- Sessions end after 30 minutes without activity and always after 12 hours.
- Users who get a password from an Admin must change it at their next sign-in.

All of these limits can be changed in *Settings → General → Security*.

## 2. Credentials

Create SNMP credentials first: *Settings → Credentials → Add*.

- **SNMP v2c:** the read-only community string.
- **SNMP v3:** the user name, security level, authentication protocol and password (SHA-256 or better
  recommended), and privacy protocol and password (AES recommended). The engine ID is discovered automatically.

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

You can add your own threshold, state-change or event rules.

- **Scope:** a rule can apply to specific devices, groups, device types or tags.
- **Duration:** how long a condition must last before the alert is raised.

## 5. Settings

| Section | Defaults |
|---|---|
| Monitoring | Poll interval 60 s; Down after 3 failed polls; 3 pings, 1000 ms timeout; SNMP timeout 3000 ms, 1 retry; 64 parallel polls |
| Security | Idle timeout 30 min; absolute 12 h; lockout after 5 failures for 15 min; minimum password length 10 |
| Retention | Raw samples 7 days; 5-minute averages 90 days; hourly averages 730 days; events, audit and closed alerts 365 days |

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

Logs are plain text, one file per day, kept for 30 days. For more detail, set `"Logging": { "MinimumLevel":
"Debug" }` in `appsettings.json` and restart the service. Set it back afterwards, because debug logging is
verbose.
