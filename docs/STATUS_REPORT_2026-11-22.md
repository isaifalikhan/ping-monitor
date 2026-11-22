# NetWatch 2.0.0 completion status report

**Date:** 2026-11-22 (review ran 2026-11-21 23:50 to 2026-11-22 00:15 PKT)
**Code under review:** committed `HEAD` = `fabcfa4` on `fix/simulator-stable-boot-time`. Most checks ran on `8c18af7`; the build and all tests were re-run on `fabcfa4`.
**Rule applied:** only committed code that I built, ran or tested counts. Uncommitted work is listed separately and earns no credit.

## 1. Summary

NetWatch has moved from 68.3% to **89.2% overall (+20.9 points)**. Work packages WP0–WP10 are implemented and committed:

- Domain controller security-log collection for all 26 event IDs (the 23 required and three more), DC rules, DC health and the DC page.
- Switch duplex, port-flap tracking, LLDP/CDP topology, GPU and iLO/iDRAC hardware.
- All seven reports, built-in backup with portable credentials, NOC/kiosk mode, connection tests and a diagnostic bundle.

The test status is good:

- **265/265 backend tests and 11/11 vitest tests pass.**
- The build has 0 errors.
- I exercised 132 API checks on a live simulator and only 3 failed, all caused by my test script.
- Authorization, the CSRF header, rate limits, security headers and SignalR all work.

The project is not 2.0.0-ready yet, for these reasons:

1. **The release build is broken at HEAD.** `build.ps1` publishes with locked restore, and the committed `packages.lock.json` files are missing `Microsoft.NET.ILLink.Tasks`. Five projects therefore fail with NU1004, and no 2.0.0 installer can be built from the repository as committed. **Fixed after this review** (see the addendum).
2. **No installed-service run and no install, upgrade-from-1.2.0 or uninstall tests** have been done.
3. **The WP12 documents, the version bump to 2.0.0 and the 2× performance results are not committed.** Another session is writing them right now.
4. Nothing has been tested against real devices yet.

Another Claude session edited and committed files during this review. Two commits landed after I started (`4c5aa82`, `fabcfa4`), and 18 files are still uncommitted (section 4.1). That session's draft `docs/STATUS_REPORT_2.0.0.md` claims 97% and says the 2.0.0 installer was built. Neither holds for the committed code (sections 4.2 and 5).

## 2. Work packages

Status scale: DONE (100), WORKING BUT INCOMPLETE (60–90), PARTIAL (20–50), STUB (1–10), NOT STARTED (0), PENDING PILOT (done in simulator, needs real devices).

How each item was verified:
- **R**: run against a live simulator or through the API.
- **T**: covered by an automated test that passed in this review.
- **C**: code read only.

| WP | Status | % | Evidence | Missing |
|---|---|---|---|---|
| **WP0** Stabilise | Working, incomplete | 80 | Phase 4 committed (`40b85f4`). 9 migrations applied cleanly on an empty database (log, R). `StartupTests`: boots on an empty folder, no pending model changes on either context (T). `--self-test` passed (R). `--Https:Port=8471/8472` respected (R) | Version is still `2.0.0-dev` in `Directory.Build.props` (2.0.0 is uncommitted). Committed README still says "Phase 5 (this release, 1.4.0)" |
| **WP1** DC collection | Pending pilot | 90 | `DcEventParser.FromFields` handles all 26 IDs. One sample per ID in `tests/.../Samples/Dc/` (26 files: 1102, 4624, 4625, 4672, 4719, 4720, 4722, 4723, 4724, 4725, 4726, 4728, 4729, 4732, 4733, 4738, 4740, 4741, 4743, 4756, 4757, 4767, 4768, 4771, 4776, 5136). `Every_catalogued_event_id_has_a_sample`, `Extracts_event_specific_fields` (T). `Decodes_failure_codes` via `DcEventCatalog.StatusText`, e.g. 0xC000006A, 0xC0000234 (T). `Successful_logon_filter_ignores_known_sources` (T). Back-off 1/2/4/8/16/30 min `Failed_security_reads_back_off` (T). WEF per DC `Forwarding_is_selectable_per_dc` (T). 5,000 events in under 30 s, duplicate-free on re-read `Ingestion_is_duplicate_free_and_keeps_up_with_high_volume` (T). Simulator produced 4624/4724/4768/4771/4776 in 2 min (R) | Samples are hand-written, not exported from a real DC (decision #8). Remote `EventLogSession` and WEF never run against a real DC |
| **WP2** DC rules | Done | 100 | `Wp2DcRuleTests`, one end-to-end test per rule (T): 5 failures per account, 20 per source, lockout, privileged group add (Critical), 1102/4719 (Critical), 4672 out of hours (PKT working hours), account create/delete, 4771 spike (≥ 50 in 5 min). `Every_dc_security_rule_is_covered_by_a_test` (T) | Threshold tuning during the pilot |
| **WP3** DC health | Pending pilot | 85 | Replication neighbours from `msDS-NCReplInboundNeighbors` over signed LDAP (`DcCollector`, C), `Parses_replication_neighbor` (T). DS/DFSR IDs 1311, 1864, 2042, 2087, 2088, 2213, 4012 in `DcCatalog.ReplicationEventIds` with rule "Directory/DFS replication error" (C). DC services `Default_dc_services_cover_...` (T). Skew vs PDC via Win32_UTCTime of the DC and the PDC emulator (C). TCP 389/88/53 checks `Port_check_reports_each_service_port` (T). `/api/dc/replication` returns data (R) | DNS is a TCP connect only, not a query. Never run against a real DC |
| **WP4** DC UI | Working, incomplete | 90 | `DomainControllersPage.tsx`: event viewer with filters, lockouts, group changes, replication, services/ports panel. `/api/saved-searches`, `/api/dc/security-events`, `/lockouts`, `/group-changes`, `/replication`, `/top-failures` all 200 (R). CSV export 200 `text/csv`, PKT times, formula-safe (R + T). 2 simulated DCs: SIM-DC-01 (PDC) and SIM-DC-02 (R) | I didn't check the page visually |
| **WP5** Switches | Pending pilot | 85 | Duplex via dot3StatsDuplexStatus, rule "Important port half duplex"; rule "Important port below expected speed" (T `Duplex_values_and_port_checks`). Flap tracker: ifOperStatus plus hidden ifLastChange bounces, 24 h count, rule "Port flapping" (T). ArubaOS-CX built-in profile (T, OIDs marked unverified). `snmp-profiles.json` override, ENTITY-SENSOR/HOST-RESOURCES fallback (T). LLDP/CDP from Cisco walks with de-duplication (T). `TopologyPage.tsx`; `/api/topology/links` 75 KB of simulated links (R). Dependency suggestions `Suggests_parents_from_links_rooted_at_the_core` (T); in the simulator `/api/topology/suggestions` is empty because parents are already set | ArubaOS-CX OIDs unverified. No real LLDP/CDP walk from your switches |
| **WP6** Servers | Pending pilot | 85 | GPU: Win32_VideoController filter, GPU Engine/Adapter Memory counters, nvidia-smi CSV, lspci (T, 4 tests). WinRM shell from a captured response (T). Redfish HPE iLO5 and Dell iDRAC9 system/thermal/power/storage (T). SNMP fallback cpqHe / globalSystemStatus (T). Simulated GPUs on 3 servers (T). Rules: GPU temp/util/memory/missing, "Server hardware component not OK", "Management controller unreachable" (C). `HardwarePanels.tsx`; `/api/devices/{id}/server` returns data (R) | The WinRM nvidia-smi path has never run on a real server. iLO/iDRAC tested only with sample documents. The newer ThermalSubsystem schema is not read |
| **WP7** Reports | Done | 95 | PDF: built-in writer, no third-party library, so nothing to license (decision #22; PDF 1.4 checked by tests). All 7 types generated live as PDF (`%PDF-` header) and CSV, plus on-screen preview, all 200 (R). Capacity CSV hit the 10/min rate limit in my loop, which is by design; covered by `Wp7ReportTests` (T). Schedule CRUD validates input: 400 "time must be HH:mm" (R); scheduler `Phase5Tests` (T) | Email delivery of scheduled reports needs a real SMTP relay |
| **WP8** Backup | Working, incomplete | 90 | "Backup now" gave a 1.1 MB zip with metrics (R). Settings default daily 02:00, keep 14 (R). Portable AES-GCM/PBKDF2 export: `Portable_backup_restores_credentials_with_the_password`, `Wrong_password_changes_nothing` (T). Console restore via `restore-pending.json`: `Pending_restore_runs_once_at_start_up` (T). Damaged zip rejected (T). Round trip `Phase5Tests` (T). `--restore` CLI | Restore from the console relies on the service manager restarting NetWatch (`failureflag`). Never verified with an installed service |
| **WP9** NOC | Working, incomplete | 90 | `/noc` route outside the layout (`NocPage.tsx`, `App.tsx`). Tiles, open alerts, DC ticker, rotating panels with pause on hover, fullscreen, sound button (C). Kiosk display links: `Kiosk_sessions_never_idle_out...`, `Revoking_a_display_ends_its_session`, `Kiosk_accounts_cannot_sign_in_with_a_password` (T). Font `clamp(14px,0.85vw,34px)` (C) | Not checked visually at 1080p or 4K |
| **WP10** Pilot tools | Pending pilot | 85 | `ConnectionTestService`: ping, SNMP v2c/v3, WinRM, EventLog, SSH, vCenter, Redfish, LDAP, SMTP, receivers. Ping test 200 OK with steps (R). SNMP without a credential gives a clear error (R). Bundle generated (11.9 KB, R): **I inspected the zip**; 0 hits for either admin password, no DPAPI blobs, secret-named settings replaced (R + T `Bundle_contains_..._no_secret`). Discovery is scan, review, then add selected (C) | 9 of 10 tests need real targets. The bundle over-redacts (`minPasswordLength` is blanked) |
| **WP11** Hardening | Partial | 60 | `TlsPolicy`: revocation NoCheck or Offline, `DisableCertificateDownloads`, applied to vCenter, Redfish, LDAPS and SMTP (T `Tls_policy_never_goes_online`). PFX import checks key, EKU, RSA ≥ 2048 and names (T). Lock files for all projects (C). `build.ps1 -SignCertificate` (C, no certificate to test). Endpoint authorization: Viewer gets 403 on 8 admin/write calls, anonymous gets 401, sign-in endpoints only (R + T). CSRF header missing gives 403 (R). Login rate limit: 6 of 15 attempts got 429 (R). HSTS, CSP, nosniff, XFO, Referrer-Policy and CORP present (R). Cookie HttpOnly/Secure/Strict (T). No passwords in logs or the bundle (R) | **Locked restore breaks publish (NU1004)**. `nuget.config` still has `signatureValidationMode=accept` (no package signature enforcement). `security-review.md` uncommitted. 2× performance results uncommitted (my own measurement is in section 4.5) |
| **WP12** Delivery | Partial | 35 | Vitest 11/11 pass (`logic.test.ts`, `components.test.tsx`). `ApiAuthIntegrationTests` and `LiveUpdatesIntegrationTests` (T). SignalR verified live: WebSocket transport, `summary` ×4, `deviceStatus`, `alertRaised` in 40 s (R) | Never run as an installed service. Install, upgrade-from-1.2.0 and uninstall keep/delete not tested (`UpgradeFrom120Tests.cs` is uncommitted). CHANGELOG, DC, reports, backup, pilot-checklist, troubleshooting and security-review docs plus `STATUS_REPORT_2.0.0.md` are all uncommitted. The committed admin guide doesn't mention GPU, LLDP/CDP, duplex, kiosk, pilot tools or the diagnostic bundle |

## 3. Overall percentage (same sections and weights as the 68.3% report)

Section averages are plain means of the items. New 2.0 items are mapped as follows:
- A9 = WP11 hardening.
- H10 = pilot tools (WP10).
- H11 = automated UI, API and SignalR tests (WP12).
- H12 = installed-service run and installer tests (WP12).

| Section | Items (%) | Avg | Weight | Contribution | Before |
|---|---|---|---|---|---|
| A Core | A1 90, A2 100, A3 100 (SignalR now verified), A4 100, A5 65 (LDAP test tool, still no AD), A6 100, A7 100, A8 100, A9 60 | 90.6 | 15% | 13.58 | 13.9 |
| B Inventory | B1 100, B2 100, B3 80, B4 90, B5 100 | 94.0 | 5% | 4.70 | 4.7 |
| C Switches | C1 100, C2 90, C3 80, C4 100, C5 90 (was 50), C6 100, C7 100, C8 85 (was 70), C9 85, C10 100, C11 100, C12 90 (was 40), C13 85, C14 85 (was 0) | 92.1 | 20% | 18.43 | 15.7 |
| D Servers | D1 85, D2 90, D3 85, D4 80, D5 GPU 80 (was 0), D6 iLO/iDRAC 85 (was 0), D7 90 | 85.0 | 15% | 12.75 | 9.1 |
| E VMware | E1 80, E2–E5 90 each, E6 85, E7 75 | 85.7 | 10% | 8.57 | 8.6 |
| F Domain controllers | F1 90, F2 95, F3 95, F4 100, F5 85, F6 90 | 92.5 | 15% | 13.88 | 1.6 |
| G Alerts | G1–G4 100, G5 95, G6 100, G7 90, G8 90, G9 80, G10 90 | 94.5 | 10% | 9.45 | 9.5 |
| H Dashboard/delivery | H1 90, H2 NOC 90, H3 100, H4 reports 95, H5 scheduled 85, H6 backup 90, H7 installer 60 (was 90), H8 docs 60 (was 80), H9 simulator 100, H10 pilot tools 85, H11 tests 90, H12 installed-service/installer tests 0 | 78.75 | 10% | 7.88 | 5.3 |
| **Overall** | | | | **89.2%** | 68.3% |

13.58 + 4.70 + 18.43 + 12.75 + 8.57 + 13.88 + 9.45 + 7.88 = **89.24%**, which is **+20.9 points** since the last report.

Strictness notes:
- F2/F3 are held at 95 because the samples are synthetic.
- E6 stays at 85 although its fix is in: vCenter events were never stored from 1.2.0 until `4c5aa82` (21 Nov 23:50). The previous 85% for E6 was therefore too generous.

## 4. Build, tests, bugs, security, air gap, performance

### 4.1 Repository state

- **Commits since 1.2.0 (`0db5e71`):**
  - Phase 4 (`40b85f4`, 1.3.0)
  - Phase 5 (`5efb975`, 1.4.0)
  - WP0 `5e49cdf`
  - WP1 `4f21a5b`
  - WP2 `d4c1d57`
  - WP3 `78c8a1d`
  - WP4 `a06575f`
  - WP5 `7c00f46`, `fb948bf` ("fix"), `a3af60a`
  - WP6 `eae731b`
  - WP7 `addc992`
  - WP8 `ff40a5e`
  - WP9 `98894e9`
  - WP10 `21dfdd7`
  - WP11 part 1 `8c18af7`
  - `4c5aa82` (vCenter events fix)
  - WP12 tests `fabcfa4`
- **Concurrent editing:** yes. Another Claude session committed `4c5aa82` (23:50:36) and `fabcfa4` (00:00:11) during this review. At the end, 18 files were uncommitted:
  - Version 2.0.0 in `Directory.Build.props` and `build.ps1`.
  - README, admin, installation and design guides.
  - `netwatch.iss`.
  - New docs: CHANGELOG, STATUS_REPORT_2.0.0, backup-restore, DC, pilot checklist, reports, security review, troubleshooting.
  - `UpgradeFrom120Tests.cs`.

  None of it counts here.

### 4.2 Build and tests

| Check | Result |
|---|---|
| `dotnet build` (Release, locked restore) at `8c18af7` and `fabcfa4` | 0 errors, 1 warning. The warning comes from building a clean export without the UI built first; it disappears after `vite build` |
| Backend tests at `8c18af7` | 263 run, 263 passed, 0 failed |
| Backend tests at `fabcfa4` | **265 run, 265 passed, 0 failed** |
| Vitest at `fabcfa4` | **2 files, 11 tests, 11 passed** (at `8c18af7`: "No test files found") |
| `tsc -b` | 0 errors |
| `--self-test --simulate` on an empty folder | Passed |
| Simulator start (9 migrations) | Clean, no pending migrations |
| `build.ps1 -Version 2.0.0` as committed | **FAILS**: NU1004 in Core, Data, Alerts, Collectors and Web. Their lock files lack `Microsoft.NET.ILLink.Tasks`, which single-file publish adds. Reproduced on `fabcfa4` with `dotnet restore -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:NetWatchLockedRestore=true` |
| Same build with locked restore disabled (in my scratch copy only) | `NetWatch-Setup-2.0.0.exe` built, 51.5 MB |
| Install, upgrade-from-1.2.0, uninstall tests | **Not done.** Your installed `D:\NetWatch` is version **1.1.0** and is stopped |

### 4.3 Bugs found

1. **Release build broken by lock files** (WP11). See above. Fix: regenerate the lock files with the publish properties, or add `Microsoft.NET.ILLink.Tasks` to every project's lock graph, then re-run `build.ps1` end to end.
2. **vCenter events were never stored** from 1.2.0 to 1.4.0/WP11: EF could not translate `data.Events.Min(...)` inside the query. Fixed and tested in `4c5aa82` (`Wp11RegressionTests`).
3. **EF warning, 4 times during the API run:** "row limiting operator ('Skip'/'Take') without an 'OrderBy'". Some paged query can return rows in an unstable order. Low impact.
4. **Diagnostic bundle over-redaction:** `minPasswordLength` is shown as `***removed***` because its name contains "password". Harmless, but it hides a useful setting.
5. **Committed binary:** `installer/NetWatch.Installer/Output/NetWatch-Setup-1.1.0.exe` is tracked in git. That's repository bloat; it should be removed and ignored.

### 4.4 Security

| Item | Status |
|---|---|
| API authorization | Good. Viewer gets 403 on users, credentials, backups, bundle, device create, security settings, connection tests and audit; anonymous gets 401 (R) |
| CSRF | Missing header gives 403 (R) |
| Login rate limit | Works: 429 after about 9 attempts per minute (R) |
| Security headers | HSTS, CSP, nosniff, X-Frame-Options, Referrer-Policy and CORP all present (R) |
| Diagnostic bundle | No passwords and no DPAPI blobs (R) |
| Session cookie | Strict and HttpOnly (T) |
| NuGet packages | `signatureValidationMode=accept`: package signatures are not enforced. Lock files pin content hashes, which partly mitigates this, but locked mode currently breaks the release build |
| Code signing | Optional and untested (no certificate). Unsigned exes may be blocked by Smart App Control |
| SNMP v2c | Clear-text community; the UI now warns about it |
| Security review | Only uncommitted (`docs/security-review.md`). No independent penetration test |

### 4.5 Air gap

| Check | Result |
|---|---|
| `scripts/check-offline.mjs` | **Passed** (no external resources in the UI bundle) |
| Source grep for external URLs | None outside comments and XML namespaces |
| Telemetry | None. `DOTNET_CLI_TELEMETRY_OPTOUT=1` in the build |
| Revocation checks | Never online: `X509RevocationMode.NoCheck`/`Offline` with `DisableCertificateDownloads=true` for vCenter, Redfish and LDAPS; MailKit `CheckCertificateRevocation=false` plus the shared policy |
| Build machine | `nuget.config` points at nuget.org; the air-gapped path is `build.ps1 -PrepareOffline` then `-Offline` (not re-tested) |

### 4.6 Performance

My own 5-minute measurement on `fabcfa4`, because the other session's 2× results are not committed. Simulator load:
- `NETWATCH_SIM_SCALE=2`: 305 devices, 12,260 live interfaces.
- 1,200 DC events per minute.
- 5% slow devices (2–2.8 s) and 1% SNMP timeouts (3 s).
- 16-core PC.

| Minute | Polls (cumulative) | Failed | Overruns | Queued | Avg poll | Max poll | Dropped samples | CPU (all cores) | RAM |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 305 | 1 | 0 | 0 | 621 ms | 5.6 s | 0 | 1.6% (start-up) | 286 MB |
| 3 | 921 | 6 | 0 | 0 | 333 ms | 5.7 s | 0 | 0.1% | 274 MB |
| 5 | 1,540 | 13 | 0 | 0 | 262 ms | 4.2 s | 0 | 0.1% | 298 MB |

- **Slow-device isolation:** works. Timeouts push the maximum to about 5.7 s, but the queue stays at 0 and there are no overruns.
- **Database growth:** 19.6 MB of metrics, 5.1 MB of events and 7.7 MB of config after about 6 minutes. That is roughly 3.3 MB/min raw metric ingest at 2×, about 4.7 GB/day before rollups and retention. A real day-long run is needed for a net growth figure.
- **Caveat:** simulated polls cost almost no CPU. Real SNMP/WinRM cost is untested.
- **Your disk:** C: had **10.5% free (15.7 GB)**, just above the 10% self-check threshold.

## 5. Remaining work, in priority order

| # | Task | Estimate |
|---|---|---|
| 1 | ~~Fix the locked-restore publish failure~~: fixed in `build.ps1` and verified end to end (addendum) | done |
| 2 | Review and commit the WP12 docs, CHANGELOG, version 2.0.0, README and `UpgradeFrom120Tests` (in progress in the other session). Correct its STATUS_REPORT_2.0.0 claims (97%, installer built) | 0.5–1 d |
| 3 | Installer tests on a clean Windows Server VM: clean install, upgrade from `NetWatch-Setup-1.2.0.exe`, uninstall keep/delete. Run as the installed service, including console restore with automatic restart (`failureflag`) | 1–1.5 d |
| 4 | 2× performance test write-up: run at least 1 h, record poll time, CPU, RAM, DB growth per day, overruns, slow-device isolation; commit the results | 0.5–1 d |
| 5 | `nuget.config` `signatureValidationMode=require` with trusted signers (nuget.org repository certificate) and a check that the offline cache still restores | 0.5 d |
| 6 | Visual UI pass: NOC at 1080p and 4K, DC page, topology, hardware panel, reports preview, Pilot tools | 0.5–1 d |
| 7 | Small fixes: EF OrderBy warning, bundle over-redaction, remove the committed 1.1.0 installer binary | 0.25 d |
| 8 | Real-device pilot (section 6) and the fixes it turns up | 5–8 d |

**Total:** about 4–6 working days to a release candidate, plus 5–8 days of pilot. That's **roughly 2–3 weeks to 100%**.

## 6. Pending pilot (done in simulation, needs real devices)

- **Domain controllers:**
  - Remote Security-log reads and WEF.
  - Replace the 26 synthetic samples with `wevtutil` exports.
  - Replication over LDAP; DS/DFSR events; time skew vs the PDC; port checks.
  - Rule thresholds with real traffic volume.
- **Switches:**
  - SNMP v2c and v3 on 2–3 models.
  - Duplex and below-speed alerts; flap counts.
  - ArubaOS-CX health OIDs (marked unverified).
  - LLDP/CDP neighbours and topology suggestions.
- **Servers:**
  - WinRM/DCOM and SSH collection.
  - GPU via WinRM remote shell and nvidia-smi.
  - iLO 5/6 and iDRAC 9 Redfish, plus the SNMP fallback.
- **VMware:** a real vCenter (login, inventory, events now that the bug is fixed, alarms, certificate pinning).
- **Authentication and mail:**
  - LDAP/LDAPS sign-in and group-to-role mapping.
  - SMTP alerts and scheduled report emails.
- **Receivers:** SNMP traps and syslog from real devices (no trap arrived in any simulator run).
- **Pilot tools:** every connection test against a real target; a diagnostic bundle with real SNMP walks.
- **Installed service and installer:**
  - Run as the installed service.
  - Installer: clean, upgrade from 1.2.0, uninstall.
  - Console restore with automatic restart.
  - Internal-CA certificate import on the server.

## 7. What I need from you

- **Decide who finishes:** another session is completing WP11/WP12 right now. Let it commit, then ask for a re-check, or tell me to take over.
- **A clean Windows Server 2019/2022 VM (snapshot)** for the installer tests. The installer needs admin rights, which I don't have in this session.
- **DC access:** a read-only account in "Event Log Readers" on a test DC, and one `wevtutil qe Security /q:"*[System[EventID=NNNN]]" /c:1 /f:xml` export per event ID.
- **Switches:** models and OS versions (ArubaOS-CX?), read-only SNMP v2c/v3 on 2–3 switches, and one `snmpwalk -On` per model (the diagnostic bundle can produce these).
- **Servers:** a read-only iLO/iDRAC account, one GPU server (with Remote Management Users rights), and one Linux server with SSH.
- **Other systems:** a read-only vCenter account, the SMTP relay, and an LDAP test account and group.
- **Certificates:** your internal CA's server certificate (PFX) for the console, and a code-signing certificate, if you have one.
- **Disk space:** free some space on C:. It is at 10.5%, near the self-check alarm.

## Addendum: release build fixed (2026-11-22 00:21 PKT, committed with this report)

**Note:** while this fix was being committed, the other session had a different, uncommitted edit in its working copy: it removes `-p:NetWatchLockedRestore=true` from the publish step instead. Don't keep that. It is not needed with this fix, and it drops the lock-file hash check from the build that produces the release exe.

**Cause.** The publish step in `build.ps1` passed `-p:PublishSingleFile=true`, `-p:IncludeNativeLibrariesForSelfExtract=true` and `-p:EnableCompressionInSingleFile=true`. Command-line properties are global, so they also reached the five library projects. There they turned on the single-file analyzer, which adds `Microsoft.NET.ILLink.Tasks`, a package their lock files don't list. The service project already sets all three in `NetWatch.Service.csproj`, so the flags were redundant.

**Fix.** I removed the three flags from the publish command in `build.ps1` and added a comment saying why. The lock files are unchanged, and locked restore stays on for both test and publish.

**Verification** (clean export of `fabcfa4` plus the fixed `build.ps1`, `build.ps1 -Version 2.0.0`, exit 0):
- UI built, vitest 11/11 passed, offline check passed.
- Backend tests 265/265 passed.
- Locked publish produced a single `NetWatch.Service.exe` (56.3 MB, compressed, self-contained), and its `--self-test` passed.
- Inno Setup built `NetWatch-Setup-2.0.0.exe` (51.5 MB).

**Effect on the score.** H7 (installer) goes back from 60 to 85; it still has no install, upgrade or uninstall test. That raises section H from 78.75 to 80.8 and the **overall to 89.4%**.
