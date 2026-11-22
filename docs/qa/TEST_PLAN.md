# NetWatch 2.0.0 — release test plan

Scope: the whole product (service, API, UI, collectors, alerting, reports, backup, installer, build) in simulator mode
at full scale (120 switches, 20 servers, 40 VMs, 4 ESXi hosts + vCenter, 2 DCs) unless stated; soak at 2× scale.
Results: [TEST_RESULTS.md](TEST_RESULTS.md). Bugs: [BUGS.md](BUGS.md).

**How tests are executed**

| Kind | Where | How |
|---|---|---|
| Automated .NET | `tests/NetWatch.Tests` (xUnit) | `dotnet test NetWatch.slnx -c Release` |
| Automated UI | `src/NetWatch.Web/ClientApp` (vitest) | `npm test` |
| Black-box API | `tests/blackbox/run.mjs` against a running instance | `node run.mjs https://host:port <dataDir> <adminPw> out.json` |
| Soak | `tests/blackbox/soak.mjs` (samples `/api/system/status` + process counters every minute) | `NETWATCH_SIM_SCALE=2 …` |
| Manual / scripted | fault injection, restore, air gap, offline build, docs | steps below |
| Pilot | real devices in the air-gapped network | [RELEASE_REPORT.md](RELEASE_REPORT.md) § Pending pilot |

## 2.1 Installation & service

| ID | Test | Steps | Expected |
|---|---|---|---|
| INS-01 | Clean install, Windows Server 2022 | Fresh VM; run `NetWatch-Setup-2.0.0.exe` as admin, defaults | Service *NetWatch* Running, Automatic (Delayed), recovery = restart; firewall rules TCP 8443, UDP 162, UDP/TCP 514; `C:\ProgramData\NetWatch` ACL SYSTEM+Administrators; desktop/start shortcut; https://host:8443 opens |
| INS-02 | Clean install, Windows 10/11 | as INS-01 | as INS-01 |
| INS-03 | Reboot | Reboot the server after a day of collection | Service starts by itself, polling resumes within 2 min, no data lost (graphs continuous except the reboot gap) |
| INS-04 | Upgrade 1.2.0 → 2.0.0 | Install 1.2.0, add data, install 2.0.0 over it | DB migrated; devices, settings, credentials, alert history, users kept |
| INS-05 | Uninstall keep-data / delete-data, re-install over kept data | Uninstall twice with both answers; re-install after keep | keep: data folder remains and is used again; delete: folder removed; service and firewall rules gone |
| INS-06 | Self-test of the built program | `NetWatch.Service.exe --self-test --data-dir <dir>` | exit 0, "Self-test passed" |
| INS-07 | Run as installed service | All tests against the installed service | as in console mode |
| INS-08 | First sign-in | One-time password from `initial-admin-password.txt` | Signed in but every API call 403 until the password is changed |
| INS-09 | HTTPS | Connect with TLS 1.2/1.3 | Console and API only over HTTPS |

## 2.2 Login, users & security

| ID | Test | Expected |
|---|---|---|
| SEC-01 | API without session | 401 |
| SEC-02 | State-changing call without `X-NetWatch-Request` (CSRF) | 403 |
| SEC-03 | Admin login, `/api/auth/me` | 200, role Admin |
| SEC-04 | Wrong password; unknown user | 401, identical generic message (no user enumeration) |
| SEC-05 | Create Operator and Viewer with "must change password" | Sign-in forces change before any API call |
| SEC-06 | Weak passwords (short, contains user name) | 400 |
| SEC-07 | NOC kiosk display link | Read-only session; cannot acknowledge; kiosk account cannot password-login |
| SEC-08 | Lockout after N failures, admin unlock | 423 while locked even with the right password; unlock → sign-in works |
| SEC-09 | Logout | Old cookie → 401 |
| SEC-10 | Session cookie | HttpOnly, Secure, SameSite=Strict |
| SEC-11 | Security headers | CSP self-only, nosniff, frame-ancestors none, no Server header |
| SEC-12 | Viewer reads/writes | Reads 200; writes and admin areas 403 |
| SEC-13 | Operator administration | Users, credentials, settings, backups, diagnostics, kiosks 403 |
| SEC-14 | Credentials never returned | No secret in any credentials API response |
| SEC-15 | XSS in device name/notes/location, alert comment | Stored verbatim, served as JSON; UI shell does not reflect it |
| SEC-16 | SQL injection in every search/filter parameter (12 endpoints × 5 payloads) | No 5xx, no data leak, tables intact |
| SEC-17 | Command injection in connection-test address and script data | Not executed |
| SEC-18 | Path traversal on backup and report file endpoints (encoded `../`, `..\`) | Refused, no file outside the folders |
| SEC-19 | 11 MB body; 5,000-deep JSON | 400/413, service stays up |
| SEC-20 | Malformed JSON / wrong types on POST endpoints | 400, never 500 |
| SEC-21 | Audit trail | Login, failed login, lockout, user/device/credential changes, import, acknowledgements, backups, reports recorded |
| SEC-22 | Secrets at rest | No test password/community in any `.db`, `-wal`, `-shm` (UTF-8 and UTF-16) |
| SEC-23 | Secrets in logs | No test password/community in any log file |
| SEC-24 | Login brute force | 429 after 10 attempts/min per client |
| SEC-30 | Permission matrix, executed | Every API endpoint (137) × anonymous / kiosk / Viewer / Operator / Admin: anonymous 401, kiosk/Viewer 403 on writes, Operator 403 on admin endpoints, Admin never refused, no 5xx |
| SEC-31 | LDAP/AD sign-in with group → role mapping | Highest mapped role wins; no cleartext bind; users outside mapped groups refused |
| SEC-32 | Session timeouts | Idle 30 min ends session; activity extends; absolute 12 h; kiosk no idle timeout; revoke immediate |

## 2.3 Inventory

| ID | Test | Expected |
|---|---|---|
| INV-01 | Add, edit, delete a switch | 201 / 2xx / 204; 404 after delete |
| INV-02 | Add each device type (Windows/Linux server, VM, DC, ESXi, router, firewall, other) | Created |
| INV-03 | Validation: bad IP, text IP, empty/blank name, unknown group/credential, 5,000-char name, duplicate IP; 1 s interval | 400/409; interval clamped to ≥ 15 s |
| INV-04 | CSV import with bad rows and duplicates | Valid rows created; each bad row reported with its line number |
| INV-05 | CSV import 500 rows | 500 devices created and listed |
| INV-06 | Empty / binary / 6 MB CSV | Clean 400/413, no 500 |
| INV-07 | Group tree: nested groups, move device, cycle, delete | Cycle refused; empty group deleted |
| INV-08 | Maintenance on a device and on a group (window) | Device shows maintenance; window accepted |
| INV-09 | Discovery dry run on a /28; /8 and garbage CIDR | Job completes; /8 and garbage refused |

## 2.4 Switches

| ID | Test | Expected |
|---|---|---|
| SW-01 | Port traffic and utilization on 10 switches | Every interface has bps; utilization 0–100 %; no negative rates |
| SW-10 | Counter math: 32/64-bit wrap, reboot (sysUpTime reset), long gap, impossible spike | Correct deltas; no rate after reboot or gap |
| SW-11 | SNMP v2c / v3 (authNoPriv, authPriv SHA/AES, wrong password) | Collected; wrong password → clear error |
| SW-12 | Health per vendor profile (Cisco, ProCurve, ArubaOS-CX, Juniper, generic) | CPU, memory, temperature, fans, PSU from sample walks |
| SW-20 | Important ports, expected speed, half duplex, link flap | Alerts only for important ports; speed/duplex/flap alerts |
| SW-30 | Syslog UDP + TCP (octet counting + newline), RFC 3164 + 5424 over real sockets | Stored, mapped to the device, severity parsed, searchable, severity filter |
| SW-31 | Traps v1 coldStart/authenticationFailure, v2c linkDown/linkUp over real sockets | Stored, named, mapped to the device and ifIndex |
| SW-40 | Port grid and graphs 1 h/24 h/7 d/30 d | Data returned < 2 s; UI renders |
| SW-41 | LLDP/CDP topology | Neighbours parsed, links de-duplicated, suggestions |

## 2.5 Servers

| ID | Test | Expected |
|---|---|---|
| SRV-01 | Windows: CPU, RAM, disks, network, uptime, reboot, services, event logs, pending reboot; WinRM and DCOM | Values from parsers/simulator |
| SRV-02 | Linux SSH/SNMP: CPU, load, RAM, mounts, services | Parsed |
| SRV-03 | GPU inventory, utilization, NVIDIA temperature/power; no GPU → no panel | Parsed from samples |
| SRV-04 | iLO/iDRAC Redfish (HPE, Dell samples), SNMP fallback, component failure → alert | Parsed; alert |
| SRV-05 | Unreachable server, wrong credential, timeout | Clear error, no crash, others unaffected |

## 2.6 VMware

| ID | Test | Expected |
|---|---|---|
| VMW-01 | Inventory: hosts, VMs, datastores, snapshots | 4 hosts, 40 VMs, datastores present |
| VMW-02 | Certificate pinning, wrong password, events/alarms import | Pinned on first use; mismatch refused; events stored |
| VMW-03 | vCenter that accepts TCP and never answers | Connection test: "did not answer in time"; polled device: "API not reachable" alert after 180 s; other polling unaffected |

## 2.7 Domain controllers

| ID | Test | Expected |
|---|---|---|
| DC-01 | Viewer: event-ID catalog and filter per ID | Every filter returns only that ID |
| DC-02 | All 26 collected event IDs (23 required + 3 more): sample in → fields parsed | Fields as in the sample |
| DC-03 | Bookmarks: service killed mid-stream and restarted | No duplicate (DeviceId, RecordId); no gap |
| DC-04 | Volume: ≥ 1,000 events/min sustained (soak: 4,000/min for 4 h) | No ingest backlog, no drops |
| DC-05 | Every DC rule fires and does not fire falsely | As in `Wp2DcRuleTests` |
| DC-06 | Replication matrix, DS/DFSR, services, time skew, reachability | As in `Wp3DcHealthTests` |
| DC-07 | DC that is gone (unreachable) | Clear error, no crash |

## 2.8 Alerts & notifications

| ID | Test | Expected |
|---|---|---|
| ALR-01 | Alerts from the simulator; summary | Active alerts present |
| ALR-02 | Acknowledge with comment, comment, close; history; audit | State changes recorded |
| ALR-03 | Bulk acknowledge 20 | Done |
| ALR-04 | Rule CRUD; unknown key / missing operator / negative duration | 400 for invalid |
| ALR-05 | Channel test buttons without configuration | Clear answer, no 500 |
| ALR-06 | Recipient/sender without domain | 400 |
| ALR-10 | Threshold: exactly at limit for 10 min (GreaterThan) | No alert |
| ALR-11 | Threshold: above for < duration / ≥ duration; dip restarts the timer | Raised only after the full duration |
| ALR-12 | Hysteresis: between clear and limit stays open; at clear → resolved with notification | As stated |
| ALR-13 | LessOrEqual at limit / just above; per-instance de-dup | As stated |
| ALR-14 | Repeated problem → one alert; ack, comment, close; new alert after close | As stated |
| ALR-15 | Dependency (unreachable upstream) and maintenance suppression | No alert; suppression listed |
| ALR-16 | Escalation after rule time | Escalated + notification |
| ALR-17 | Engine restart with open alerts | Reloaded; still auto-resolve |
| ALR-18 | Flapping | Held open, resolved after stable period |
| ALR-19 | Device deleted while its signals are queued | Dropped, engine continues |
| ALR-20 | SMTP through a relay with AUTH | Mail with subject, body, PKT time, valid recipients only |
| ALR-21 | Digest for bursts, severity filter | 1 digest for ≥ 5; below-minimum not sent |
| ALR-22 | Relay briefly down | Delivered on retry |
| ALR-23 | Syslog forward UDP/TCP | RFC 5424, escaped SD, octet counting, UTF-8 |
| ALR-24 | Script action: env + stdin, no injection, exit code, timeout, name validation | As stated |
| ALR-25 | Windows Event Log write | Entry in Application log, source NetWatch |
| ALR-30 | Pop-up + sound in 2 browsers (SignalR); reconnect | Both sessions receive; reconnect after blip |

## 2.9 Dashboard, reports, NOC, backup

| ID | Test | Expected |
|---|---|---|
| DSH-01 | Overview totals vs device list | Equal (± live drift) |
| DSH-02 | 42 main GET endpoints with full data | Each < 2 s |
| UI-01 | Every page in Edge, Chrome, Firefox; 1080p and 4K; dark/light; console | < 2 s, no console errors |
| RPT-01 | 7 reports × PDF and CSV | Valid PDF / CSV |
| RPT-02 | Empty range, reversed range, very large range | 200 / 400 / 400 (366-day limit) |
| RPT-03 | Scheduled report run now | File saved and listed |
| NOC-01 | NOC mode: kiosk link, no timeout, rotation, sound | As `Wp9NocTests`, SEC-07, SEC-32 |
| BAK-01 | Backup now; download; content | Valid zip with the 3 databases; no plain secret |
| BAK-02 | Corrupted and truncated backup | Restore refused, nothing scheduled |
| BAK-03 | Portable backup password policy | Weak password 400 |
| BAK-04 | Restore into a new data folder (same machine) | Command succeeds; restored folder self-tests; contents equal the backup |
| BAK-05 | Restore on a different machine with the portable password | Credentials usable there |

## 2.10 Pilot tools

| ID | Test | Expected |
|---|---|---|
| PLT-01 | Connection tests: ping timeout, SNMP no agent, SSH refused, WinRM unreachable, vCenter refused, SMTP not configured/unresolvable, LDAP not configured, receivers | Each a clear message, no 500 |
| PLT-02 | Diagnostic bundle | Valid zip; no test password/community inside |

## 2.11 Reliability, performance & soak

| ID | Test | Expected |
|---|---|---|
| REL-01 | Soak 2× (240 switches, ~12,500 interfaces, 4,000 DC events/min, 5 % slow, 2 % timeouts) ≥ 4 h | RAM flat after warm-up, 0 overruns, 0 dropped samples, no growing queue; record CPU, RAM, handles, DB growth |
| FLT-01 | Service killed (`taskkill /F`) mid-write, restarted | DBs pass integrity check; open alerts and counter states reloaded; polling resumes |
| FLT-02 | All 3 databases exclusively locked for 45 s | No crash; reads keep working; errors logged; full recovery after release |
| FLT-03 | vCenter that hangs | see VMW-03 |
| FLT-04 | DC that disappears | see DC-07 |
| FLT-05 | Disk almost full | Self-check alert; no corruption |
| FLT-06 | 5 % slow / 2 % timeout devices | Part of REL-01 |
| REL-20 | Retention over simulated months/years | Only expired data removed; open alerts kept; history cascades; sessions purged; idempotent |
| REL-21 | Roll-ups | 30-day and 400-day graphs still show data |
| REL-22 | Time: stored UTC, shown PKT; device clock wrong; change of day | UTC in DB; PKT in UI/mail; receive time used; day shards |

## 2.12 Air gap

| ID | Test | Expected |
|---|---|---|
| AIR-01 | Outbound connections of the running instances for 60 min | Zero non-loopback TCP endpoints |
| AIR-02 | `check-offline.mjs`; scan of sources for external URLs | Pass; only XML namespace identifiers |
| AIR-03 | `build.ps1 -Offline` with an empty NuGet cache and HTTP(S) forced to a dead proxy | Build, tests, exe and installer succeed |
| AIR-04 | Isolated VM, firewall logging incl. DNS/CRL/OCSP, 1 h | Zero attempts |

## 2.13 Documentation

| ID | Test | Expected |
|---|---|---|
| DOC-01 | Installation guide, step by step | Every step works as written |
| DOC-02 | Admin guide, DC guide, backup guide, pilot checklist | Consistent with the product |
