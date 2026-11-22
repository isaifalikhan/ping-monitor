# NetWatch 2.0.0 — test results (release QA, 2026-11-22)

Test plan: [TEST_PLAN.md](TEST_PLAN.md) · bugs: [BUGS.md](BUGS.md) · summary: [RELEASE_REPORT.md](RELEASE_REPORT.md)

**Environment.** Build PC: Windows 11 Pro 10.0.26200 (not elevated), .NET SDK 10.0.404, Node 24.22, Inno Setup 6.
Instances run from the Release build in console mode with `--simulate` on separate data folders and ports
(9443 soak, 9444 functional). UDP 162/514 on this PC are held by another, pre-existing NetWatch dev instance, so the
receivers were tested on free ports (SW-30/31). **Not available here:** administrator rights, a Windows Server 2022
VM, real devices, an LDAP server, a browser that trusts the self-signed console certificate.

**Result values:** PASS (run and seen passing) · FAIL (bug ID) · NOT RUN (could not be executed here; reason given) ·
PENDING PILOT (passes with simulator/sample data; must be proven on real devices).

**Automated suites (final runs).**

| Suite | Run | Passed | Failed | Skipped |
|---|---|---|---|---|
| xUnit (`dotnet test`, Release, inside the offline build of `2afc75e`) | 304 | 304 | 0 | 0 |
| vitest (UI) | 11 | 11 | 0 | 0 |
| Black-box API (`tests/blackbox/run.mjs`) against the **release exe** | 53 | 53 | 0 | 0 |

## 2.1 Installation & service

| ID | Result | Evidence |
|---|---|---|
| INS-01 | **NOT RUN** | Needs a fresh Windows Server 2022 VM and administrator rights (installer creates a service, firewall rules, ACLs). Installer script reviewed: service `delayed-auto`, failure actions restart, 4 firewall rules, ACL SYSTEM + Administrators, shortcut |
| INS-02 | **NOT RUN** | as INS-01 (not run on this PC: it would change system/firewall settings) |
| INS-03 | **NOT RUN** | needs the installed service |
| INS-04 | PASS (database) / **NOT RUN** (installer) | `UpgradeFrom120Tests.A_120_database_upgrades_with_its_data`: real 1.2.0 schema + data → all migrations, data kept. Installer-over-1.2.0 not executed |
| INS-05 | **NOT RUN** | needs the installer |
| INS-06 | PASS | `StartupTests`; `--self-test` on a restored data folder exit 0 ("Self-test passed") |
| INS-07 | **NOT RUN** | all runtime tests used console mode (`UseWindowsService` host; same code path except SCM) |
| INS-08 | PASS | `ApiAuthIntegrationTests`, black-box SEC-05 (403 until changed) |
| INS-09 | PASS | every black-box and integration test runs over HTTPS (TLS 1.2/1.3 only, Kestrel config) |

## 2.2 Login, users & security

| ID | Result | Evidence |
|---|---|---|
| SEC-01…SEC-24 | PASS (24/24) | black-box run, e.g. SEC-08 "locked after 5 failures", SEC-16 60 injection requests no 5xx, SEC-18 7 traversal variants refused, SEC-19 11 MB → 400, SEC-24 `401×10, 429×4` |
| SEC-11 | PASS | CSP `default-src 'self'; script-src 'self'; … frame-ancestors 'none'`, `nosniff`, no `Server` header |
| SEC-22 | PASS | all `*.db`, `-wal`, `-shm` scanned for 8 test secrets (UTF-8 + UTF-16): 0 hits |
| SEC-23 | PASS | all log files scanned for the same secrets: 0 hits |
| SEC-30 | PASS | `QaPermissionMatrixTests`: 137 endpoints × 5 identities executed against the real host; 0 violations, 0 server errors; matrix saved as `permission-matrix.csv` |
| SEC-31 | PASS (unit) / PENDING PILOT | `Phase2ParserTests.Ldap_*` (name normalisation, filter escaping, highest role wins, no cleartext bind); no LDAP server here |
| SEC-32 | PASS | `QaSessionTests` (3): idle 30 min, activity extends, absolute 12 h, kiosk no idle timeout, revoke immediate |

## 2.3 Inventory

| ID | Result | Evidence |
|---|---|---|
| INV-01…INV-09 | PASS (9/9) | e.g. INV-04 bad IP/name/type and duplicates reported per line; INV-05 500 rows in 122 ms; INV-03 1 s interval stored as ≥ 15 s; INV-09 /8 and garbage CIDR refused |

## 2.4 Switches

| ID | Result | Evidence |
|---|---|---|
| SW-01 | PASS | 520/520 interfaces on 10 switches with traffic, utilization 0–100 % |
| SW-10 | PASS | `CounterMathTests` (10): 32-bit wrap, 64-bit decrease = reset, reboot, gap, spike |
| SW-11 | PENDING PILOT | v2c/v3 code paths only against the simulator; wrong v3 password message not testable without an agent |
| SW-12 | PASS (samples) / PENDING PILOT | `Wp5SwitchTests` on walk samples; ArubaOS-CX OIDs unverified |
| SW-20 | PASS | `Wp5SwitchTests` (duplex, expected speed, flap tracker), `AlertEvaluatorTests.StateChange_ImportantInterfacesOnly` |
| SW-30 | PASS | `QaReceiverIntegrationTests`: UDP RFC 3164 + 5424, TCP octet-counted + newline; mapped to device; severities 2/4/6/3; `maxSeverity` filter |
| SW-31 | PASS | same test: v2c linkDown/linkUp (ifIndex 17 in summary), v1 coldStart/authenticationFailure; counters on status page; 0 parse errors |
| SW-40 | PASS (API) / **NOT RUN** (browser) | 30-day interface series < 2 s (DSH-02); UI component tests (vitest); browser check see UI-01 |
| SW-41 | PASS | `Wp5SwitchTests` LLDP/CDP parsers, de-duplication, suggestions |

## 2.5 Servers

| ID | Result | Evidence |
|---|---|---|
| SRV-01, SRV-02 | PASS (sim/parsers) / PENDING PILOT | `Phase2ParserTests`, simulator data on the server page |
| SRV-03 | PASS (samples) / PENDING PILOT | `Wp6HardwareTests` GPU parsers (WMI, nvidia-smi, lspci) |
| SRV-04 | PASS (samples) / PENDING PILOT | `Wp6HardwareTests` Redfish HPE iLO 5 / Dell iDRAC 9 samples, SNMP fallback |
| SRV-05 | PASS | PLT-01: unreachable WinRM "RPC server is unavailable", missing DC "firewall rule may be blocked", polling of others unaffected (FLT-03) |

## 2.6 VMware

| ID | Result | Evidence |
|---|---|---|
| VMW-01 | PASS | 4 hosts, 40 VMs, 3 datastores |
| VMW-02 | PASS (sim) / PENDING PILOT | `Phase3VmwareTests` (pinning, events stored) |
| VMW-03 | PASS | listener that accepts and never answers: connection test "vCenter did not answer in time" after 15 s, API responsive; as a polled device the "vCenter/ESXi API not reachable" alert fired within 7 min (rule 180 s), device Warning; 0 overruns elsewhere |

## 2.7 Domain controllers

| ID | Result | Evidence |
|---|---|---|
| DC-01 | PASS | 26 catalog IDs; each `eventIds=` filter returned only that ID |
| DC-02 | PASS (samples) / PENDING PILOT | `Wp1DcSecurityTests`, `Phase4DcTests`: 26 sample events, all fields; samples to be replaced by real `wevtutil` exports |
| DC-03 | PASS (sim) | after `taskkill /F` mid-stream and restart: 0 duplicate (DeviceId, RecordId) among 1,079 → 1,245 events |
| DC-04 | PASS | soak: 4,000 events/min for 4 h (4× the requirement), ingest queue 0, 0 dropped |
| DC-05 | PASS | `Wp2DcRuleTests` (every rule fires and does not fire falsely) |
| DC-06 | PASS | `Wp3DcHealthTests` |
| DC-07 | PASS | Event-log connection test to an unreachable address: clear error after 21 s, no crash |

## 2.8 Alerts & notifications

| ID | Result | Evidence |
|---|---|---|
| ALR-01…ALR-06 | PASS (6/6) | black-box; ALR-06 = BUG-002 regression |
| ALR-10…ALR-19 | PASS (9 tests) | `QaAlertLifecycleTests` — real AlertManager + database + manual clock; stable over 3 runs |
| ALR-20…ALR-24 | PASS (10 tests) | `QaNotificationTests` — fake SMTP relay with AUTH, digest, retry (BUG-001), syslog UDP/TCP, script (BUG-003) |
| ALR-25 | **NOT RUN** | the Event Log source is registered by the installer (HKLM, admin); not elevated here |
| ALR-30 | PASS (2 sessions) / **NOT RUN** (reconnect) | `LiveUpdatesIntegrationTests.A_new_alert_reaches_two_browser_sessions`; network-blip reconnect needs a browser |

## 2.9 Dashboard, reports, NOC, backup

| ID | Result | Evidence |
|---|---|---|
| DSH-01 | PASS | overview totals = device list |
| DSH-02 | PASS | 42 endpoints, max 41 ms, avg 12 ms (full simulator data) |
| UI-01 | **NOT RUN** | the in-app browser refuses the self-signed console certificate; no trusted certificate on this PC. vitest 11/11; check-offline pass |
| RPT-01 | PASS | 14 files (7 × PDF/CSV), PDFs start with `%PDF-` |
| RPT-02 | PASS | empty range 200, reversed 400, 26-year range 400 (366-day limit) |
| RPT-03 | PASS | schedule "run now" saved a file; e-mail delivery uses the SMTP channel tested in ALR-20 |
| NOC-01 | PASS | `Wp9NocTests`, SEC-07, SEC-32 (kiosk) |
| BAK-01…BAK-03 | PASS (3/3) | zip with 3 databases, no plain secret; corrupt and truncated zips → 400, nothing scheduled |
| BAK-04 | PASS | `--restore` into a new folder: exit 0, previous files kept in `before-restore-…`, restored folder `--self-test` exit 0; rules 61/61, credentials 17/17, users 13/13, settings 12/12 equal to the backup |
| BAK-05 | PENDING PILOT | needs a second server (DPAPI is per machine); portable export unit-tested in `Wp8BackupTests` |

## 2.10 Pilot tools

| ID | Result | Evidence |
|---|---|---|
| PLT-01 | PASS (after BUG-007) | first run: SMTP unresolvable host lacked the host name; final run on the release exe: ping/SNMP/SSH/WinRM/vCenter/SMTP/LDAP/receivers all give a clear message |
| PLT-02 | PASS | 8 files in the bundle; 0 hits for 8 test secrets |

## 2.11 Reliability, performance & soak

| ID | Result | Evidence |
|---|---|---|
| REL-01 (soak 1, 237 min) | **FAIL** (BUG-006) | 1 overrun in ≈ 72,000 polls; otherwise 0 dropped samples/alerts/messages, queue 0, CPU 2.5 % of one core, working set 307 → 342 MB, handles 915 → 1,065, DB growth 2.8 GB/day at 4,000 DC events/min |
| REL-01 (soak 2, final build) | see RELEASE_REPORT | running at the time of writing |
| FLT-01 | PASS | `taskkill /F` during collection; `PRAGMA quick_check` ok on all 3 DBs; restart reloaded 37 open alerts and 6,332 counter states; polling resumed, 0 overruns |
| FLT-02 | PASS | 3 DBs exclusively locked 45 s: reads OK, writes logged errors after 30 s busy timeout, ~35 polls counted failed, 0 overruns/drops; no error after release; integrity ok |
| FLT-03 | PASS | see VMW-03 |
| FLT-04 | PASS | see DC-07 |
| FLT-05 | **NOT RUN** | filling the system disk of the build PC is not acceptable; self-check exists (`SelfMonitorService`) |
| FLT-06 | PASS | part of REL-01 profile (5 % slow, 2 % timeouts) |
| REL-20, REL-21 | PASS | `QaRetentionTests`: data at 1/30/400/1,200 days; only expired data removed, open alerts kept, history cascaded, sessions purged, idempotent; 30-day and 400-day graphs still served |
| REL-22 | PASS | DB timestamps UTC (entities), PKT in mails (`ALR-20` asserts " PKT") and UI (`LocalTime`); receive time used for syslog with wrong device clock (SW-30 sends a device timestamp hours off); day shards `MetricStoreTests.TablesInRange_SpansDays` |

## 2.12 Air gap

| ID | Result | Evidence |
|---|---|---|
| AIR-01 | PASS (TCP) | 60-min sampler of both running instances: 0 distinct non-loopback TCP endpoints. DNS/CRL/OCSP attempts cannot be attributed per process without admin → AIR-04 |
| AIR-02 | PASS | `check-offline.mjs` pass; source scan finds only XML namespace identifiers (w3.org, schemas.microsoft.com, xmlsoap.org — never fetched) |
| AIR-03 | PASS (after BUG-008) | first run failed (`MSB1001`). Final: clean `git worktree` of `2afc75e`, only `offline` linked, empty `NUGET_PACKAGES`, HTTP(S) to a dead proxy (loopback exempt for the tests): `npm ci --offline`, UI build + offline check, 304/304 tests, single-file exe 56.3 MB, installer 51.5 MB, exit 0 |
| AIR-04 | **NOT RUN** / PENDING PILOT | needs an isolated VM with firewall logging (admin) |

## 2.13 Documentation

| ID | Result | Evidence |
|---|---|---|
| DOC-01 | PARTIAL | reviewed against code and installer script; one error fixed (moving to another server: portable backup). Step-by-step execution needs the installer (INS-01) |
| DOC-02 | PARTIAL | admin, backup, DC guides and pilot checklist reviewed; consistent; not executed on real devices |
