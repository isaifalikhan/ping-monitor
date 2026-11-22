# Pilot test checklist

Work through this list on the real network after installing NetWatch 2.0. Tick each line, note anything that
fails, and bring out a **diagnostic bundle** (*Settings → Pilot tools*) for every open point.

## A. Installation

- [ ] `NetWatch-Setup-2.0.0.exe` installs; service *NetWatch* is Running; console opens on https://\<server\>:8443
- [ ] Initial admin password from `C:\ProgramData\NetWatch\initial-admin-password.txt` works; password changed
- [ ] Internal-CA certificate imported (*Settings → System status → HTTPS certificate*), no browser warning after restart
- [ ] Firewall rules present: TCP 8443, UDP 162, UDP/TCP 514
- [ ] Upgrade test: install over 1.2.0 on a copy of production data → devices, history, users kept
- [ ] Uninstall keep-data → data folder still present; re-install picks it up
- [ ] Uninstall delete-data (on a test machine) → `C:\ProgramData\NetWatch` removed

## B. Switches (per vendor: Cisco, HP/Aruba ProCurve, ArubaOS-CX, Juniper)

- [ ] Pilot tools → SNMP test passes (v3 authPriv where possible) and lists interfaces
- [ ] Device added; interfaces, traffic and utilization appear after two polls
- [ ] CPU, memory, temperature, fans and PSU shown — **ArubaOS-CX: compare with the bundle's walk of 1.3.6.1.4.1.47196.4.1.1.3.11 and correct `snmp-profiles.json` if needed**
- [ ] Duplex shown in the port grid; an important port set to half duplex alerts
- [ ] Expected speed set on an uplink; forcing it to 1G alerts
- [ ] Unplug/replug a port 4 times in 10 minutes → *Port flapping*
- [ ] LLDP/CDP neighbours appear (Topology → Discovered links); suggestions make sense
- [ ] Trap test (e.g. link down) and syslog received (Pilot tools → Syslog/traps received)

## C. Servers

- [ ] Windows: Pilot tools → WinRM/WMI passes; disks, services, patches, event log errors appear
- [ ] Linux: SSH test passes; disks and services appear; host key pinned
- [ ] GPU servers: GPU panel shows utilization and memory; NVIDIA temperature/power via `nvidia-smi` (WinRM remote shell needs *Remote Management Users*)
- [ ] iLO / iDRAC: Redfish test passes with a read-only account; hardware panel lists fans, PSUs, temperatures, drives; pull a PSU cable → alert
- [ ] Server without Redfish account: SNMP fallback reports the overall health

## D. VMware

- [ ] vCenter test passes (Read-only role at the root); hosts, VMs, datastores, snapshots listed
- [ ] A test snapshot older than 7 days (or >50 GB) alerts; vCenter events are listed on the VMware page

## E. Domain controllers

- [ ] Audit policy GPO applied (dc-guide.md §2); Security log ≥ 1 GB
- [ ] Pilot tools → Remote Event Log test passes for every DC (or WEF configured)
- [ ] 5 wrong passwords for a test account → *Failed logons for one account*; lockout → *Account locked out*
- [ ] Add a test account to Domain Admins (and remove it) → Critical alert, Group changes tab
- [ ] Replication matrix green; clock skew < 30 s; services and ports green
- [ ] **Export real sample events** for the parser tests: `wevtutil qe Security /q:"*[System[EventID=4625]]" /c:1 /f:xml` (repeat per event ID) and bring them out

## F. Alerts, notifications, reports, backup

- [ ] SMTP test mail arrives; a Critical alert is e-mailed
- [ ] Syslog/Event Log forwarding reaches the SIEM (if used)
- [ ] Each report generates as PDF and CSV; a daily schedule saves and e-mails
- [ ] Backup now works; portable backup restored on a test server with its password; console restore restarts the service
- [ ] NOC display link on the wall screen; stays signed in overnight; revoke signs it out

## G. Load and stability (first week)

- [ ] *Settings → System status*: no overruns, no dropped samples, poll durations stable
- [ ] Self-checks green; database growth roughly as expected (security-review.md, performance)

## H. Items not executed in release QA (must pass before production — see docs/qa/RELEASE_REPORT.md)

- [ ] **Installer on Windows Server 2022** (clean VM): service *NetWatch* Running, *Automatic (Delayed Start)*, recovery = restart; `Get-NetFirewallRule -DisplayName "NetWatch*"` shows 4 rules; `icacls C:\ProgramData\NetWatch` = SYSTEM + Administrators only; shortcut opens the console
- [ ] **Reboot** after one day of collection: service starts by itself, *System status* polling resumes within 2 minutes, graphs continue
- [ ] **Upgrade 1.2.0 → 2.0.0** with the real installer on a copy of production data (ROLLBACK.md §1 first): devices, users, credentials (connection test passes), alert history kept
- [ ] **Uninstall keep / delete** and **re-install over kept data**
- [ ] **Windows Event Log channel**: *Settings → Notifications → Event Log → Test* → entry in *Application* log, source *NetWatch*
- [ ] **Browsers**: every page in Edge, Chrome and Firefox (F12 console without errors), 1080p and 4K, dark and light theme, page load < 2 s; pop-up + sound in two browsers; unplug the operator PC's network for 30 s → live updates resume
- [ ] **Air gap**: enable Windows Firewall logging of dropped *and* allowed connections plus DNS client logging for 1 hour; only monitored devices, the SMTP relay and the DCs appear — no public DNS names, no CRL/OCSP
- [ ] **Restore on a second server** with a portable backup and its password; credential connection tests pass there
- [ ] **Disk almost full**: on a test server fill the data drive below the self-check threshold → *NetWatch disk space low* alert, no database error after freeing space
- [ ] **Soak**: one week in production, *System status* shows 0 overruns (if an overrun occurs, attach the log lines "Poll overrun: …" — BUG-006)
