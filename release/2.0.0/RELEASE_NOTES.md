# NetWatch 2.0.0 — release notes

**Status of this package: RELEASE CANDIDATE — NO-GO for production installation until the open items in
`docs/qa/RELEASE_REPORT.md` are closed** (installer tests on Windows Server 2022, BUG-006, pilot items).

NetWatch is an offline monitoring service for an air-gapped network: one Windows service with its own HTTPS
console, three SQLite databases, one installer, no internet access at install or run time.

## What is new since 1.2.0

- **Domain controllers (1.3 + 2.0):** Security-log collection for the 23 required event IDs (remote reads or
  Windows Event Forwarding per DC, bookmarks, back-off, 4624 filters); brute-force, lockout, privileged-group,
  log-cleared, audit-policy, off-hours admin, account create/delete and Kerberos-spike rules; replication matrix,
  DS/DFSR, AD services, clock skew and port checks; DC page with event viewer, saved searches and CSV export.
- **Switches:** duplex and expected-speed checks, link-flap detection, LLDP/CDP topology and upstream
  suggestions, ArubaOS-CX and editable vendor profiles (`snmp-profiles.json`).
- **Servers:** GPU inventory/utilization/NVIDIA temperature and power; iLO/iDRAC hardware health over Redfish with
  SNMP fallback.
- **Reports (1.4 + 2.0):** 7 reports as PDF and CSV, schedules with e-mail, VMware and capacity-trend reports,
  preview.
- **Backup:** scheduled and manual backups, verified restore, portable credential export for a new server, restore
  from the web console.
- **NOC:** wall-display kiosk links (read-only, no idle timeout, revocable), group tiles, DC ticker, 4K scaling.
- **Pilot tools:** connection tests for every protocol and a diagnostic bundle without secrets.
- **Security/air gap:** no online revocation checks, internal-CA certificate import, locked package restore,
  optional code signing, every API endpoint authorization-tested.
- **Release QA fixes:** notification retry, e-mail address validation, script action stdin, offline build, clearer
  connection-test errors, overrun diagnostics (see `docs/qa/BUGS.md`).

## Install / upgrade

- New installation: `docs/installation-guide.md` (INSTALL_GUIDE.pdf in this folder).
- Upgrade from 1.2.0: **copy the data folder first** (ROLLBACK.md §1), then run the installer. Databases are
  migrated automatically on first start. Built-in rules keep their thresholds (see CHANGELOG, upgrade notes).
- Rollback: ROLLBACK.md.
- Verify the download: `Get-FileHash NetWatch-Setup-2.0.0.exe -Algorithm SHA256` must match `SHA256SUMS.txt`.
- The installer and exe in this package are **not code-signed** (no signing certificate was provided); allow them
  by hash or path in AppLocker/WDAC, or rebuild with `build.ps1 -SignCertificate`.

## Requirements

Windows Server 2019/2022/2025 or Windows 10 1809+/11, 64-bit; 2 vCPU / 4 GB RAM for ~300 devices; 20 GB disk minimum.
Ports: TCP 8443 in (console), UDP 162 in (traps), UDP/TCP 514 in (syslog), outbound per monitored protocol.

## Known limitations

See `docs/qa/RELEASE_REPORT.md` § Known limitations (simulator-only verification of device protocols, UI not
verified in a browser during QA, single-server deployment, PDF reports in English only).
