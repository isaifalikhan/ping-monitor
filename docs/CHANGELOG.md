# Changelog

## Unreleased

- **Static MACs from HIT SIEM:** admins add or remove a static MAC address on a switch port (TP-Link and Juniper)
  from the port details; HIT SIEM runs the CLI commands over SSH, saves the configuration and writes every attempt to
  the audit log. Device form: *SSH credential (CLI)* for switches and routers.
- **Device name per MAC:** the MAC tables, port details and MAC search show the connected device's name (HIT SIEM
  device with that IP, LLDP name, or DNS name) next to its IP. Port details show the *Connected device*.
- Switches and routers with an SSH credential are no longer polled as Linux servers.

- **DC Security log backfill:** on the first remote read of a DC, the last 24 h still in its Security log are read
  as well (*Settings → General → Domain controllers*, 0–720 h, 0 = only new events). Backfilled events are stored
  and searchable but raise no alerts.
- **CSV export** of syslog, SNMP traps, Windows event logs and alerts with the same filters as the lists (newest
  100,000 rows, audited), next to the existing DC security-event export.
- **Longer search ranges:** 90 days and 1 year on *Events & logs* and *Alerts → History*, 90 and 365 days in the DC
  event viewer. *Alerts → History* now filters by time range and text on the server, so alerts older than the newest
  1,000 can be found.
- Syslog and trap lists are sorted by time instead of by arrival order.

## 2.0.0

**Domain controllers**
- Security-log collection for all 23 required event IDs, 26 in total (4624–4776, 4719, 1102, 5136, group changes …), remote
  reads or Windows Event Forwarding selectable per DC, bookmarks, back-off for failing DCs, 4624 filters
  (logon types, ignored sources).
- New rules: Kerberos pre-authentication spike, account deleted; Account Operators privileged by default.
- Health: replication failing for 1 h, replication stale (no success for 3 h), clock skew critical at 2 min,
  LDAP 389 / Kerberos 88 / DNS 53 reachability; default AD services also for CSV-imported DCs.
- DC page: CSV export of the event viewer, services and ports on the DC cards.

**Switches**
- Duplex (EtherLike-MIB) and expected-speed checks on important ports; link-flap detection (ifOperStatus and
  ifLastChange, 4 changes in 10 minutes) with a 24 h flap count.
- LLDP and CDP neighbours, discovered-links topology graph, upstream-device suggestions.
- ArubaOS-CX health profile; vendor profiles can be corrected in `snmp-profiles.json` without a new build.

**Servers**
- GPUs: inventory, utilization and memory (WMI / lspci), NVIDIA temperature, power and fan via `nvidia-smi`
  (WinRM remote shell or SSH); four GPU rules.
- iLO / iDRAC hardware health over Redfish (certificate pinning) with HPE/Dell SNMP fallback; hardware panel
  and rules.

**Reports**: VMware report; server CPU/memory trends and disks predicted to fill within 30 days; most-error
interfaces; off-hours admin logons and log-cleared events; on-screen preview.

**Backup**: portable credential export for restoring on a new server; restore from the web console; default
time 02:00.

**NOC**: kiosk display links (read-only, no idle timeout, revocable), group tiles, DC security ticker, 4K
scaling, sound toggle.

**Pilot tools**: connection tests (ping, SNMP, WinRM/WMI, event log, SSH, vCenter, Redfish, LDAP, SMTP,
syslog/traps) and a diagnostic bundle with every secret removed.

**Security and build**: outgoing TLS never checks revocation online (NoCheck or Offline); internal-CA
certificate import for the console; NuGet lock files with locked restore; optional code signing; endpoint
authorization enforced by tests; SNMP v2c warning.

**Fixes**: vCenter events were never stored (query translation error); `--Https:Port` and `NETWATCH_*`
environment variables now override `appsettings.json`.

**Release QA fixes (2026-11-22)**: SMTP and syslog notifications are retried after a transient relay failure;
e-mail addresses without a domain are refused; a notification script that ignores stdin no longer fails;
`build.ps1 -Offline` works (it failed at the test step); clearer SMTP/SNMP connection-test errors; poll overruns are
logged with device and timing. See docs/qa/BUGS.md.

**Upgrade notes**: databases are upgraded automatically (migrations Phase4 … Wp9Kiosk). Built-in rules that
already exist keep their thresholds: to use the 2.0 defaults set *AD replication failing* to 3600 s and *DC clock
skew critical* to 120 s in *Settings → Alert rules*.

## 1.4.0
Reports (PDF/CSV, schedules), backups, self-monitoring, NOC wall display, topology tree.

## 1.3.0
Domain controller monitoring (Security log, replication, clock), DC page, DC simulator.

## 1.2.0
VMware monitoring through the vSphere API.

## 1.1.0
Server monitoring (WinRM/WMI, SSH), traps and syslog, alert lifecycle, notifications, AD sign-in.

## 1.0.0
Windows service with HTTPS console, inventory, ping and SNMP polling of switches, alerts, installer.
