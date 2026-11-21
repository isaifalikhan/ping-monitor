# Changelog

## 2.0.0

**Domain controllers**
- Security-log collection for all 23 required event IDs (4624–4776, 4719, 1102, 5136, group changes …), remote
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
