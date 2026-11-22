**NO-GO** — NetWatch 2.0.0 is not yet cleared for installation in the production network. The product is complete and
passes every test that could be run here (304/304 automated, 53/53 black-box on the release exe), but the installer
has never been executed on Windows Server 2022, one Medium bug (BUG-006) is open, and 12 test cases could not be run
on this build PC. See §1 for exactly what blocks GO.

# NetWatch 2.0.0 — release report (2026-11-22)

Release QA lead report. Inputs: [TEST_PLAN.md](TEST_PLAN.md), [TEST_RESULTS.md](TEST_RESULTS.md),
[BUGS.md](BUGS.md). Previous report: [../STATUS_REPORT_2.0.0.md](../STATUS_REPORT_2.0.0.md).

- **Tested commit:** `2afc75e` (branch `fix/simulator-stable-boot-time`); the release package was built from a clean
  checkout of that commit with `build.ps1 -Offline`. Later commits change documentation only.
- **Overall completion: 97 %** (96.7 %; unchanged since the last report — no feature was missing; QA added tests and
  fixed 7 bugs). 100 % in simulator mode; the rest is pilot work on real devices.
- **Bugs:** found 8 — Critical 0, High 1, Medium 4, Low 3 · fixed 7 · **open: C/H/M/L = 0/0/1/0** (BUG-006).
- **Release package:** `release/2.0.0/` (installer, SHA256SUMS, release notes, install guide PDF, pilot checklist PDF,
  rollback). **No `v2.0.0` tag was created**, because the gate is NO-GO.

Stage lines:

- Stage 1 done – overall 97%, bugs open: C/H/M/L = 0/0/0/0 (none known yet)
- Stage 2 done – overall 97%, bugs open: C/H/M/L = 0/1/4/3
- Stage 3 done – overall 97%, bugs open: C/H/M/L = 0/0/1/0

## 1. Release gate

| # | Criterion | | Evidence |
|---|---|---|---|
| 1 | Build 0 errors / 0 warnings; all tests pass, none skipped; coverage targets | ✅ | 0/0 (Release, offline); xUnit 304/304, vitest 11/11, 0 skipped; coverage Core 94.6 %, Alerts 84.6 %, Collectors parsers 87.7 %, Data 90.4 % (§3) |
| 2 | Every feature DONE in simulator mode | ✅ | §2: all A1–H9 and WP0–WP12 items Done or Pending pilot; nothing Stub/Partial/Not started |
| 3 | 100 % of test cases PASS in the final regression | ❌ | 97 of 122 PASS; 12 NOT RUN, 10 PENDING PILOT, 2 PARTIAL, 1 FAIL (soak 1, BUG-006; soak 2 on the final build PASS, §5a) — §4 |
| 4 | 0 open Critical / High / Medium | ❌ | BUG-006 (Medium) open — not reproduced in soak 2, cause unproven |
| 5 | Install, reboot, upgrade from 1.2.0, uninstall PASS on Windows Server 2022 | ❌ | NOT RUN: needs an admin VM; database upgrade from 1.2.0 passes (`UpgradeFrom120Tests`) |
| 6 | Soak: no leaks, no overruns, recovery from every injected fault | ❌ | soak 1: 1 overrun; working set +35 MB over 3.5 h (not proven flat); fault recovery PASS (kill, DB lock, hung vCenter, missing DC); disk-full NOT RUN; final soak: §5 |
| 7 | Security: permission matrix, no injection/XSS, no secrets in DB/logs/bundle, security review | ✅ | SEC-01…32; executed matrix 137 endpoints × 5 identities; 0 secret hits in DBs, logs, backup, bundle; `docs/security-review.md` |
| 8 | Air gap: zero outbound internet attempts; offline build | ⚠️ | 0 non-loopback TCP connections in 60 min ✅; offline build from clean checkout ✅; DNS/CRL/OCSP attempts not observable without admin (AIR-04 NOT RUN) |
| 9 | Backup/restore incl. a new machine | ⚠️ | backup, corrupt-file rejection, restore into a new folder ✅; restore on a second machine NOT RUN (pilot) |
| 10 | Documentation verified step by step | ❌ | reviewed against code and installer, 1 error fixed; step-by-step execution needs the installer |

**What blocks GO, and how to clear it**

1. **Installer tests (gate 5, INS-01…05, INS-07)** on a Windows Server 2022 test VM with admin rights: pilot checklist
   §A and §H. Est. ½ day.
2. **BUG-006:** run the final-soak build (overrun diagnostics) for a week in the pilot; if "Poll overrun" log lines
   appear they show whether the device was queued or running → fix → 4-hour soak with 0 overruns.
3. **Memory:** a 24-hour soak must show a flat working set after warm-up (pilot week or test VM).
4. **NOT RUN cases** that need admin rights, a trusted browser certificate or a second server: UI-01, ALR-25,
   ALR-30 (reconnect), FLT-05, AIR-04, BAK-05, DOC-01/02 → pilot checklist §H.
5. **PENDING PILOT** items (§7) — they cannot be proven without real devices; they do not need code changes unless
   the pilot finds a difference.

## 2. Feature inventory and completion

Status: DONE · PENDING PILOT (done and tested with the simulator/sample data; not yet against the real device type;
counted at 90–95 %). Evidence = class/test; "QA" = added in this release QA.

| # | Feature | Status | % | Evidence |
|---|---|---|---|---|
| A1 | Service host + scheduler | DONE | 100 | `Program`, `PollScheduler`; `StartupTests`; QA: kill/restart, overrun diagnostics |
| A2 | DB, schema, retention, roll-ups | DONE | 100 | migrations; `UpgradeFrom120Tests`; QA `QaRetentionTests` |
| A3 | HTTPS + REST + SignalR | DONE | 100 | `ApiAuthIntegrationTests`, `LiveUpdatesIntegrationTests` |
| A4 | Login, roles, audit | DONE | 100 | `Wp11SecurityTests`; QA `QaPermissionMatrixTests`, `QaSessionTests`, black-box SEC-* |
| A5 | LDAP/AD login | PENDING PILOT | 90 | `LdapAuthenticator`, `Phase2ParserTests.Ldap_*` |
| A6 | DPAPI credentials | DONE | 100 | `DpapiSecretProtector`, `Wp8BackupTests`; QA SEC-22 |
| A7 | Logging | DONE | 100 | Serilog; QA SEC-23 (no secrets) |
| A8 | Self-monitoring | DONE | 100 | `SelfMonitorService` |
| B1–B5 | Manual, CSV, discovery, groups, maintenance | DONE | 100 | QA INV-01…09 |
| C1 | Ping | DONE | 100 | `PingProbe` |
| C2–C3 | SNMP v2c / v3 | PENDING PILOT | 90 | simulator; connection test (QA BUG-004) |
| C4, C6, C7 | Port status, traffic, errors | DONE | 100 | `CounterMathTests`; QA SW-01 |
| C5 | Speed + duplex | DONE (sim) | 95 | `Wp5SwitchTests` |
| C8 | CPU/mem/temp/fan/PSU | PENDING PILOT | 90 | `SnmpHealthProfiles` (ArubaOS-CX OIDs unverified) |
| C9–C10 | Trap and syslog receivers | DONE | 100 | QA `QaReceiverIntegrationTests` (real sockets) |
| C11 | Important ports | DONE | 100 | `AlertEvaluatorTests` |
| C12 | Flap detection | DONE (sim) | 95 | `FlapTracker` tests |
| C13 | Port grid + graphs | DONE | 100 | vitest; API < 2 s (DSH-02) |
| C14 | LLDP/CDP topology | DONE (sim) | 95 | `Wp5SwitchTests` |
| D1–D3 | Windows metrics, services, event logs | PENDING PILOT | 95 | parsers, simulator |
| D4 | Linux | PENDING PILOT | 95 | `Phase2ParserTests` |
| D5 | GPU | PENDING PILOT | 90 | `Wp6HardwareTests` |
| D6 | iLO/iDRAC | PENDING PILOT | 90 | `Wp6HardwareTests` (Redfish samples) |
| D7 | Server UI + graphs | DONE | 100 | UI tests |
| E1–E7 | VMware | PENDING PILOT | 95 | `Phase3VmwareTests`; QA hung vCenter (VMW-03) |
| F1–F3 | DC collection, parsing, 23 IDs | PENDING PILOT | 95 | `Wp1DcSecurityTests` (26 samples); QA DC-03/04 |
| F4 | DC security rules | DONE | 100 | `Wp2DcRuleTests` |
| F5 | Replication, services, skew, ports | PENDING PILOT | 95 | `Wp3DcHealthTests` |
| F6 | DC UI | DONE | 100 | `Wp4DcUiTests` |
| G1–G10 | Alerts and notifications | DONE (SMTP relay pending pilot) | 99 | QA `QaAlertLifecycleTests`, `QaNotificationTests` |
| H1–H3 | Overview, NOC, theme | DONE | 100 | `Wp9NocTests`; DSH-01 |
| H4–H5 | Reports, schedules | DONE | 100 | `Wp7ReportTests`; RPT-01…03 |
| H6 | Backup/restore | DONE | 100 | `Wp8BackupTests`; BAK-01…04 |
| H7 | Installer | PENDING (built, not executed) | 85 | `NetWatch-Setup-2.0.0.exe` from the offline build |
| H8 | Documentation | DONE | 100 | 12 documents + QA docs |
| H9 | Simulator | DONE | 100 | |
| WP0–WP12 | Completion plan | DONE | — | commits `5e49cdf` … `a76e9ed`; WP10 pilot tools (PLT-01/02), WP11 hardening (SEC-*), WP12 delivery (this report) |

| Section | Weight | % | Contribution | Change since last report |
|---|---|---|---|---|
| A Core | 15 | 98.8 | 14.8 | — (more tests) |
| B Inventory | 5 | 100 | 5.0 | — |
| C Switches | 20 | 96.1 | 19.2 | — |
| D Servers | 15 | 93.6 | 14.0 | — |
| E VMware | 10 | 95.0 | 9.5 | — |
| F Domain controllers | 15 | 96.7 | 14.5 | — |
| G Alerts | 10 | 99.0 | 9.9 | — (4 notification bugs fixed) |
| H Dashboard/delivery | 10 | 98.3 | 9.8 | — (offline build fixed) |
| **Overall** | **100** | | **96.7 ≈ 97 %** | ±0 |

## 3. Build, tests and coverage

| Item | Result |
|---|---|
| Build | `dotnet build` Release: 0 warnings, 0 errors; `tsc -b` clean; UI offline check pass |
| xUnit | 304 run / 304 passed / 0 failed / 0 skipped (was 266; +38 QA tests in 9 new files) |
| vitest | 11 / 11 |
| Black-box API | 53 / 53 against the release single-file exe |
| Regression run 2 (HEAD `ddd3912`) | build 0/0 · xUnit 304/304 · vitest 11/11 · offline check pass · black-box 53/53 on the release exe · package hashes OK (TEST_RESULTS) |
| Coverage (line) | Core 94.6 % (was 72.0) · Alerts 84.6 % excl. generated regex (was 55.5) · Collectors parsers 87.7 % · Data 90.4 % hand-written · Collectors overall 65.3 % · Web 58.8 % (Web and collectors transport code are exercised by the integration/black-box tests, not counted in a target) |

## 4. Test results summary (final regression)

| Area | Cases | PASS | FAIL | NOT RUN | PENDING PILOT | PARTIAL |
|---|---|---|---|---|---|---|
| 2.1 Installation & service | 9 | 3 | 0 | 6 | 0 | 0 |
| 2.2 Login, users & security | 27 | 26 | 0 | 0 | 1 | 0 |
| 2.3 Inventory | 9 | 9 | 0 | 0 | 0 | 0 |
| 2.4 Switches | 9 | 6 | 0 | 1 | 2 | 0 |
| 2.5 Servers | 5 | 1 | 0 | 0 | 4 | 0 |
| 2.6 VMware | 3 | 2 | 0 | 0 | 1 | 0 |
| 2.7 Domain controllers | 7 | 6 | 0 | 0 | 1 | 0 |
| 2.8 Alerts & notifications | 23 | 21 | 0 | 2 | 0 | 0 |
| 2.9 Dashboard, reports, NOC, backup | 12 | 10 | 0 | 1 | 1 | 0 |
| 2.10 Pilot tools | 2 | 2 | 0 | 0 | 0 | 0 |
| 2.11 Reliability & soak | 10 | 8 | 1 | 1 | 0 | 0 |
| 2.12 Air gap | 4 | 3 | 0 | 1 | 0 | 0 |
| 2.13 Documentation | 2 | 0 | 0 | 0 | 0 | 2 |
| **Total** | **122** | **97** | **1** | **12** | **10** | **2** |

## 5. Performance

**Soak 1** (2× scale: 305 devices incl. 240 switches, ~12,500 interfaces; 4,000 DC events/min = 4× the requirement;
5 % slow and 2 % timeout devices; 237 min; console mode; same PC busy with builds and tests):

| Metric | Value |
|---|---|
| Polls | ≈ 72,000 (≈ 310/min = every device every 60 s) |
| Poll duration | average 450 ms, decaying max ≈ 5–7 s |
| Overruns | **1** (BUG-006) |
| Dropped samples / alert signals / receiver messages | 0 / 0 / 0 |
| Queues (poll, metric writes, alert engine, event ingest) | 0 (no growth) |
| CPU | 2.5 % of one core average |
| Working set | 307 MB at 30 min → 342 MB at 237 min (+10 MB/h, not proven flat) |
| Handles | 915 → 1,065 |
| Database growth | 2.8 GB/day before retention at 4,000 DC events/min (≈ 0.9 GB/day expected at 1,000/min) |

**§5a — Soak 2** (final build `2afc75e` with overrun diagnostics, same profile, 244 min, 2026-11-21 23:46 → 2026-11-22 03:50 UTC):

| Metric | Value |
|---|---|
| Polls | 74,860 (58 failed = the simulated 2 % timeout devices) |
| Poll duration | decaying max 5.8–6.6 s; no poll reached the 55 s timeout |
| Overruns | **0** — no "Poll overrun" log line |
| Dropped samples / alert signals / receiver messages | 0 / 0 / 0 |
| Metric write queue | average 111; short peaks up to 1,536 that drained by the next sample (each earlier peak did) |
| CPU | 2.4 % of one core average |
| Working set (hourly average) | 293 → 315 → 327 → 327 MB (flat in hours 3–4) |
| Private bytes (hourly average) | 169 → 188 → 199 → 200 MB |
| Handles | 887 → 995 (max 1,074) |
| Log errors | only the 3 expected "cannot listen" on UDP 162 / UDP+TCP 514 (ports held by a pre-existing instance on this PC) |

Result: REL-01 **PASS** on the final build. BUG-006 did not reproduce (1 overrun in ≈ 147,000 polls over both soaks),
so its cause is still unproven and it stays open; memory flattening after ≈ 2 h is encouraging but 4 h does not prove
the absence of a slow leak — the 24 h / one-week pilot run remains required.

**Response times** with full simulator data: 42 main API endpoints max 41 ms, average 12 ms (30-day interface graph
included). 500-row CSV import 122 ms.

**Fault injection:** service killed mid-write → databases intact, 37 open alerts and 6,332 counter states reloaded,
polling resumed · all three databases locked 45 s → reads fine, writes failed with logged errors, full recovery ·
hung vCenter → 15 s timeout, alert after the rule's 180 s, other polling unaffected · unreachable DC → clear error.

## 6. Security findings

- **Authorization:** executed permission matrix (137 endpoints × anonymous, kiosk, Viewer, Operator, Admin): no
  violation, no 5xx. Operators cannot manage inventory or administration (design: they work alerts).
- **Input handling:** SQL injection (60 requests), command injection, path traversal (7 variants), XSS payloads,
  11 MB bodies, 5,000-deep JSON, malformed JSON: all refused or stored inertly; never a 5xx.
- **Secrets:** 8 test secrets never found in any database/WAL/SHM file (UTF-8 + UTF-16), log, backup zip, diagnostic
  bundle or API response.
- **Sessions:** HttpOnly/Secure/SameSite=Strict cookie; idle 30 min; absolute 12 h; lockout after 5 failures; login
  rate limit 10/min per client; CSRF header required.
- **Headers:** strict same-origin CSP, `frame-ancestors 'none'`, `nosniff`, no Server header.
- **Observations (no change made):** the backup zip contains the self-signed console certificate's PFX and its
  DPAPI-protected password file (machine-bound; protect backup folders accordingly); NOC kiosk sessions may create
  saved searches (own preference only); `docs/security-review.md` remains valid.

## 7. Pending pilot — must be tested on real devices (exact steps: `docs/pilot-checklist.md`, PILOT_CHECKLIST.pdf)

| Item | Test steps (summary) |
|---|---|
| Installer, reboot, upgrade 1.2.0 → 2.0.0, uninstall keep/delete | Checklist §A + §H: copy data folder first (ROLLBACK.md §1); verify service settings, 4 firewall rules, ACL; reboot; upgrade on a copy of production data; uninstall both ways |
| SNMP v2c/v3 per vendor (Cisco, ProCurve, ArubaOS-CX, Juniper) | §B: Pilot tools → SNMP test (v3 authPriv); wrong v3 password → clear error; interfaces after 2 polls; health values vs device CLI; ArubaOS-CX walk of 1.3.6.1.4.1.47196.4.1.1.3.11 → fix `snmp-profiles.json` |
| Duplex / speed / flap / LLDP / traps / syslog on real switches | §B: half-duplex on an important port, 1G on a 10G uplink, 4 unplug/replug in 10 min, link-down trap, syslog |
| Windows (WinRM + DCOM), Linux SSH, GPU, iLO/iDRAC | §C: connection tests; disks/services/patches; nvidia-smi over WinRM; pull a PSU cable → alert |
| vCenter (read-only role), snapshots, events | §D |
| DCs: remote reads and WEF, 23 event IDs from real exports, rules, replication | §E: export samples with `wevtutil qe Security /q:"*[System[EventID=4625]]" /c:1 /f:xml` per ID and re-run the parser tests; 5 wrong passwords, lockout, Domain Admins add/remove |
| LDAP/AD sign-in with group → role mapping | Settings → Directory, LDAPS 636, map two groups, sign in with a user of each |
| SMTP relay, syslog/Event Log forwarding | §F + §H Event Log test |
| Browsers, 4K, themes, live updates, reconnect | §H |
| Air gap with firewall + DNS logging for 1 h | §H |
| Portable restore on a second server | §H |
| One-week stability, 0 overruns, flat memory | §G + §H (BUG-006) |

## 8. Known limitations

- Device protocols (SNMP, WinRM/WMI, SSH, vSphere, Redfish, Event Log, LDAP, SMTP) are verified with the simulator,
  sample data and connection tests only (§7).
- UI was not checked in a browser during this QA (self-signed certificate not trusted on the build PC); UI component
  tests and API timings pass.
- The installer and exe are not code-signed (no certificate provided); allow by hash/path in AppLocker/WDAC.
- ArubaOS-CX health OIDs unverified; Event Log source registration requires the installer (admin).
- Device status is ICMP-based; a vCenter whose API hangs stays *Up* until the 180 s "API not reachable" alert.
- Retention removes whole day/month/year shards (hourly data can be kept up to one year longer than 730 days).
- Reports are limited to 366 days per run; PDFs in English.
- Single server; no high availability. DNS/CRL activity of the service could not be audited without admin rights.
