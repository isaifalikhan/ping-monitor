# NetWatch status report — 2.0.0 (2026-11-22)

**Summary.** NetWatch 2.0.0 is feature-complete in simulator mode. Every checklist item A1–H9 and every 2.0 item
(duplex, link flap, ArubaOS-CX, GPU, iLO/iDRAC, topology, reports, backup, NOC, connection test, diagnostic
bundle) is implemented, wired end to end (collect → store → UI → alerts) and tested: **266 .NET tests and 11 UI
tests pass, the build has 0 warnings**, the real host boots on an empty database, upgrades a 1.2.0 database
without loss, serves the API over HTTPS with enforced roles and pushes alerts to two browser sessions over
SignalR. The installer `NetWatch-Setup-2.0.0.exe` is built. What is **not** proven is behaviour against real
devices inside the air gap: those items are marked *pending pilot* and limited to 90 %. The installer was built but
not executed on the build PC (it creates a service and firewall rules), so H7 is also pending.
**Overall: 97 %** (100 % in simulator mode; the remainder is the pilot).

Status scale as in the 1.2.0 report. *Pending pilot* = done and tested with the simulator and with sample data,
but not yet run against the real device type.

## 1. Project overview

| Project | Purpose | LOC (excl. migrations) |
|---|---|---|
| NetWatch.Core | Domain model, settings, metric math, TLS policy | 2,800 |
| NetWatch.Data | SQLite, metric store, retention, backups, portable secrets, console certificate | 3,000 (+ migrations) |
| NetWatch.Alerts | Rule engine, alert manager, notifications | 2,200 |
| NetWatch.Collectors | Scheduler, ping, SNMP (+ LLDP/CDP, profiles), WinRM/WMI (+ remote shell), SSH, vSphere, Redfish, DC collector, receivers, simulator | 12,100 |
| NetWatch.Web | REST API, auth (incl. kiosk), SignalR, reports, diagnostics, topology, React UI | 20,200 |
| NetWatch.Service | Windows service host, certificate, seeding, self-test, restore at start | 700 |
| NetWatch.Tests | xUnit (unit, sample-data parsers, integration) | 4,400 |

- .NET 10, EF Core Sqlite 10.0.15, Serilog, SharpSnmpLib, SSH.NET, MailKit, Cronos, CsvHelper; React 18, Vite 5,
  ECharts 5, SignalR client. **No new packages in 2.0.** NuGet lock files (content hashes) with locked restore;
  `npm ci` with package-lock.
- Build: `dotnet build` 0 warnings / 0 errors; `dotnet test` 266/266; `npm test` 11/11; UI build passes the offline
  check (no external URL).
- Windows service: host verified in console mode and by `--self-test`; installed-service run pending (H7).
- No internet at run time: no CDN, web fonts, telemetry or update checks; outgoing TLS never checks revocation
  online.

## 2. Feature checklist

| # | Feature | Status | % | Evidence |
|---|---|---|---|---|
| A1 | Service host + scheduler | Done | 100 | `Program`, `PollScheduler`; `--self-test`, `StartupTests` |
| A2 | Database, schema, retention, rollups | Done | 100 | 7 migrations after 1.2.0; `UpgradeFrom120Tests`; pending-model test |
| A3 | HTTPS + REST + SignalR | Done | 100 | `ApiAuthIntegrationTests`, `LiveUpdatesIntegrationTests` |
| A4 | Login, roles, audit | Done | 100 | `Wp11SecurityTests` (all endpoints), integration tests |
| A5 | LDAP/AD login | Pending pilot | 90 | `LdapAuthenticator` + own TLS verification; connection test; no real AD here |
| A6 | DPAPI credentials | Done | 100 | `DpapiSecretProtector`, portable export (`Wp8BackupTests`) |
| A7 | Logging | Done | 100 | Serilog; no secrets in logs (review) |
| A8 | Self-monitoring | Done | 100 | `SelfMonitorService`, System status |
| B1–B5 | Manual, CSV, discovery (dry run), groups, maintenance | Done | 100 | existing tests; CSV DC services fix |
| C1 | Ping | Done | 100 | |
| C2 | SNMP v2c | Pending pilot | 90 | simulator, connection test; no real switch |
| C3 | SNMP v3 | Pending pilot | 90 | |
| C4 | Port status | Done | 100 | |
| C5 | Speed + duplex | Done (sim) | 95 | `Wp5SwitchTests` (EtherLike walk sample), alerts in simulator |
| C6–C7 | Traffic, errors/discards | Done | 100 | |
| C8 | CPU/mem/temp/fan/PSU | Pending pilot | 90 | Cisco, HP ProCurve, Juniper code; ArubaOS-CX profile **unverified OIDs**; generic fallback |
| C9–C10 | Trap and syslog receivers | Done | 100 | |
| C11 | Important ports | Done | 100 | |
| C12 | Flap detection | Done (sim) | 95 | `FlapTracker` tests; simulator flapping ports |
| C13 | Port grid + graphs | Done | 100 | duplex/flap markers; UI tests |
| C14 | LLDP/CDP topology | Done (sim) | 95 | parsers on walk samples, simulated neighbours, graph, suggestions |
| D1–D3 | Windows metrics, services, event logs | Pending pilot | 95 | simulator; WinRM/DCOM code unchanged since 1.1 |
| D4 | Linux | Pending pilot | 95 | |
| D5 | GPU | Pending pilot | 90 | WMI/perf counters/nvidia-smi/lspci parsers on samples; WinRM shell only tested with a captured response |
| D6 | iLO/iDRAC | Pending pilot | 90 | Redfish parsers on HPE iLO 5 and Dell iDRAC 9 samples; SNMP fallback |
| D7 | Server UI + graphs | Done | 100 | GPU and hardware panels (UI tests) |
| E1–E7 | VMware | Pending pilot | 95 | event-storage bug fixed (regression test); simulator; no real vCenter |
| F1–F3 | DC log collection, parsing, 23 IDs | Pending pilot | 95 | 26 sample events, back-off, WEF per DC, 5,000-event ingest test; samples to be replaced by real exports |
| F4 | DC security rules | Done | 100 | every rule tested end to end (`Wp2DcRuleTests`) |
| F5 | Replication, services, skew, ports | Pending pilot | 95 | |
| F6 | DC UI | Done | 100 | filters, saved searches, CSV export, lockouts, groups, matrix, services/ports |
| G1–G10 | Alerts and notifications | Done (SMTP pending pilot) | 99 | existing tests; SMTP connection test |
| H1 | Overview | Done | 100 | |
| H2 | NOC | Done | 100 | kiosk tests, group tiles, ticker, 4K scaling |
| H3 | Theme | Done | 100 | |
| H4 | Reports PDF/CSV | Done | 100 | 7 reports, preview; `Wp7ReportTests` |
| H5 | Scheduled reports | Done | 100 | |
| H6 | Backup/restore | Done | 100 | portable credentials, console restore end to end |
| H7 | Installer | Pending (not executed) | 85 | `NetWatch-Setup-2.0.0.exe` built; script reviewed; install/upgrade/uninstall on a test server = pilot checklist §A |
| H8 | Documentation | Done | 100 | 11 documents (guides, DC, reports, backup, pilot, troubleshooting, security review, decisions, changelog) |
| H9 | Simulator | Done | 100 | DCs, GPUs, BMCs, LLDP, duplex/flap, ArubaOS-CX, load knobs |
| New | Connection tests, diagnostic bundle | Done | 100 | `Wp10PilotToolsTests` (no secret in the bundle) |

## 3. Completion

| Section | % | Weight | Contribution |
|---|---|---|---|
| A Core | 98.8 | 15 % | 14.8 |
| B Inventory | 100 | 5 % | 5.0 |
| C Switches | 96.1 | 20 % | 19.2 |
| D Servers | 93.6 | 15 % | 14.0 |
| E VMware | 95 | 10 % | 9.5 |
| F Domain controllers | 96.7 | 15 % | 14.5 |
| G Alerts | 99 | 10 % | 9.9 |
| H Delivery | 98.3 | 10 % | 9.8 |
| **Overall** | | | **96.7 % ≈ 97 %** |

Phases: 1 — 97 %, 2 — 96 %, 3 — 95 %, 4 — 96 %, 5 — 99 % (each limited only by pending-pilot items).

## 4. Quality and risks

- **Bugs fixed in 2.0:** vCenter events never stored (EF translation error); `--Https:Port` ignored; restore and
  test races on pooled SQLite connections.
- **Security:** see [security-review.md](security-review.md) — all endpoints authorized (enforced by tests), CSRF
  header, strict cookies, security headers, DPAPI/AES-GCM secrets, no secrets in logs or bundles, air-gap TLS
  policy, locked packages, optional signing.
- **Performance:** 2× simulator scale (240 switches, ~12,500 interfaces, ~1,000 DC events/min, 5 % slow devices,
  2 % timeouts), two 30-minute runs: average poll 0.3–0.4 s, no overruns, no queue, no dropped samples, ≈ 2 % of one CPU core, ≈ 300 MB RAM, ≈ 2 GB/day database growth before retention; details in security-review.md §2.
- **Air gap:** nothing contacts the internet; revocation NoCheck/Offline; offline build kit.
- **Not verified here:** real devices; installed-service run and installer tests; ArubaOS-CX OIDs; nvidia-smi over
  WinRM; Redfish on real controllers; DC parsing with real exports.

## 5. Next steps (pilot)

1. Installer tests on a test server (clean install, upgrade from 1.2.0, uninstall keep/delete) — 0.5 day.
2. Pilot checklist sections B–E per device type, with diagnostic bundles for failures — 2–4 days.
3. Correct `snmp-profiles.json` for ArubaOS-CX from a real walk — 0.5 day.
4. Replace the DC sample events with real exports (`wevtutil qe …`) and rerun the tests — 0.5 day.
5. Fix whatever the bundles show — 1–3 days.

Needed from the network team: a test server, read-only SNMPv3 users on 2–3 switches per vendor (incl. one
ArubaOS-CX), a DC account in Event Log Readers / Remote Management Users, a read-only vCenter user, read-only
iLO and iDRAC accounts, one GPU server, the SMTP relay, the internal CA's certificate for the console and its CRL
(if *Offline* revocation is wanted), and optionally a code-signing certificate.
