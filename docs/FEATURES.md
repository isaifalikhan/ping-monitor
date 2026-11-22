# NetWatch 2.0.0 — Feature list

This list was compiled on 2026-11-22 from the source code (Core, Data, Alerts, Collectors, Web API and React UI,
Service, installer script, tests) and the database schema and built-in rules, then checked against a running
instance in simulator mode (`--simulate`, commit `ddd3912`, 188 devices, 6,332 interfaces).

**How each feature was checked:** every API endpoint behind a page was called (47 GET endpoints, all HTTP 200
with data), the action endpoints were exercised (all 7 reports in PDF and CSV, manual and portable backups, ping
connection test, subnet discovery, diagnostic bundle, rate limiting), and the full automated suite ran (304/304
passed). Screens were checked through their code and APIs; they could not be opened in a browser in this run
because the browser on the build PC does not trust NetWatch’s self-signed certificate.

## Status legend

| Mark | Meaning |
|---|---|
| ✅ Available | Implemented and verified (API, simulator, or automated tests against the real code path) |
| 🟡 Limited | Works, with a limitation stated in the Status row |
| 🧪 Pending pilot | Works in the simulator / with sample data; not yet tested against the real device type |
| ⛔ Not available | Not in the product — listed only in table G |

## Terms used

- **SNMP** (Simple Network Management Protocol): how NetWatch reads switches; **MIB** = the list of values a device publishes; **OID** = the numeric address of one value.
- **WinRM / WMI**: Windows remote management; NetWatch asks Windows for its counters with WMI queries carried over WinRM (or over **DCOM**, the older RPC transport).
- **Redfish**: the standard REST API of server management controllers (HPE **iLO**, Dell **iDRAC**).
- **WEF** (Windows Event Forwarding): DCs push their events to a collector instead of being read remotely.
- **PDC emulator**: the DC that is the time source of the domain; **FSMO**: the special roles some DCs hold.
- **Important port**: an interface you flag as an uplink or server port; only those raise port alerts.
- **Rule keys** such as `iface.util` or `device.down` are the names used in alert rules.
- **PKT**: Pakistan Standard Time (UTC+5), used for all schedules and displays.

## Status counts

| Section | Features | ✅ | 🟡 | 🧪 |
|---|---|---|---|---|
| 1. Platform | 12 | 9 | 3 | 0 |
| 2. Security | 13 | 12 | 0 | 1 |
| 3. Device inventory | 10 | 9 | 0 | 1 |
| 4. Switch monitoring | 27 | 9 | 1 | 17 |
| 5. Server monitoring | 15 | 0 | 2 | 13 |
| 6. VMware monitoring | 11 | 1 | 0 | 10 |
| 7. Domain controller monitoring | 18 | 9 | 0 | 9 |
| 8. Alerting | 20 | 18 | 0 | 2 |
| 9. Dashboards and UI | 16 | 15 | 1 | 0 |
| 10. Reports | 12 | 10 | 1 | 1 |
| 11. Backup and restore | 7 | 6 | 0 | 1 |
| 12. Pilot and support tools | 5 | 5 | 0 | 0 |
| 13. Installation and upgrade | 8 | 2 | 1 | 5 |
| **Total available** | **174** | **105** | **9** | **60** |
| ⛔ Not available (table G) | 8 | | | |

## 1. Platform

#### PLT-01 · Windows service host

| Field | Content |
|---|---|
| What it does | NetWatch runs as one self-contained Windows service ("NetWatch Monitoring Service") that hosts the collectors, the alert engine and the web console in a single process. No .NET runtime, IIS or SQL Server is needed. |
| How it works | .NET 10 single-file `NetWatch.Service.exe`, `UseWindowsService`; runs as LocalSystem; start type *Automatic (Delayed Start)*; recovery: restart after 60 s, 60 s, 300 s (reset after 24 h). |
| What you see | Settings → System status (version, start time). |
| Alerts | “NetWatch self-check failing” (A-61) covers the service’s own health. |
| Configuration | `C:\ProgramData\NetWatch\appsettings.json`: `Https:Port` (8443), certificate, `Logging`, `Simulator:Enabled`. Command-line options: see PIL-04/PIL-05. |
| Requirements | Windows Server 2019/2022/2025 or Windows 10 1809+/11, 64-bit (installer `MinVersion=10.0.17763`). |
| Permissions | Status page: Admin. |
| Status | ✅ Available |

#### PLT-02 · Poll scheduler

| Field | Content |
|---|---|
| What it does | Polls every enabled device on its own interval, with a bounded number of polls running at once, so one slow device never delays the others. |
| How it works | Per-device worker from a pool; default interval 60 s (min 15 s, max 3600 s); max 64 parallel polls (1–512); interface table re-read every 30 min, inventory every 24 h; a poll is cancelled after its interval minus 5 s (10–120 s; 55 s at the default 60 s). |
| What you see | Settings → System status: total/failed polls, overruns, in-flight, queued, average and max duration. |
| Alerts | Polling backlog → self-check “Polling” (A-61). |
| Configuration | Settings → General → Monitoring: default poll interval 60 s, max concurrent polls 64, interface refresh 30 min, inventory refresh 24 h. Per device: poll interval. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | 🟡 Limited — BUG-006 (open, Medium): one poll overrun in ≈147,000 polls across two 4-hour soaks; cause unproven. Measured: ≈310 polls/min at 2× simulator scale, average 280–450 ms. |

#### PLT-03 · HTTPS web console

| Field | Content |
|---|---|
| What it does | The browser console is served by NetWatch itself over HTTPS only. |
| How it works | Kestrel on TCP **8443** (all addresses), TLS 1.2 and 1.3, HTTP/1.1 + HTTP/2, no `Server` header, request body limit 10 MB, 1,000 concurrent connections. The React UI is embedded in the exe. |
| What you see | Every page. |
| Alerts | “Web certificate” self-check warns 30 days before expiry (A-61). |
| Configuration | `Https:Port` in appsettings.json (service restart). |
| Requirements | Inbound TCP 8443 (firewall rule created by the installer). |
| Permissions | Any signed-in user. |
| Status | ✅ Available |

#### PLT-04 · REST API

| Field | Content |
|---|---|
| What it does | Everything the console shows or changes goes through a JSON API under `/api`; every endpoint except sign-in requires a role. |
| How it works | ASP.NET Core minimal APIs (≈137 endpoints). Policies Viewer / Operator / Admin; state-changing calls need header `X-NetWatch-Request`. |
| What you see | Used by the UI; can be scripted with a session cookie. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Per endpoint (see SEC-03). |
| Status | ✅ Available |

#### PLT-05 · Live updates (SignalR)

| Field | Content |
|---|---|
| What it does | Pages update without reload when device status, metrics or alerts change; a summary is pushed every 10 s. |
| How it works | SignalR hub `/hubs/live`; events `deviceStatus`, `deviceMetrics`, `summary`, `alertRaised`, `alertUpdated`, `alertResolved`, `alertEscalated`. |
| What you see | Overview, alert badge, port grid, NOC. |
| Alerts | Pop-up and sound (ALR-16) use these events. |
| Configuration | — |
| Requirements | WebSockets through any proxy between operators and the server. |
| Permissions | Any signed-in user. |
| Status | ✅ Available |

#### PLT-06 · Databases

| Field | Content |
|---|---|
| What it does | All data is kept in three local SQLite files: configuration, metrics and events. |
| How it works | SQLite (WAL mode). `netwatch.db` (EF Core: inventory, rules, alerts, users, settings), `metrics.db` (sharded time series), `events.db` (device events, syslog, traps, Windows events, DC security events). Schema upgrades run automatically at start. |
| What you see | Settings → System status → storage sizes. |
| Alerts | “Metric storage” self-check. |
| Configuration | — |
| Requirements | Data folder `C:\ProgramData\NetWatch\data`. |
| Permissions | Admin |
| Status | ✅ Available |

#### PLT-07 · Metric rollups

| Field | Content |
|---|---|
| What it does | Raw samples are averaged into 5-minute and hourly values so long-term graphs stay fast and small. |
| How it works | Raw data in one table per UTC day, 5-minute rollups per month, hourly per year. Graphs pick the resolution automatically: ≤ 26 h raw, ≤ 31 days 5-minute, otherwise hourly. Single writer with bounded queues (250,000 samples). |
| What you see | All graphs. |
| Alerts | Dropped samples → self-check. |
| Configuration | Retention (PLT-08). |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### PLT-08 · Retention purge

| Field | Content |
|---|---|
| What it does | Old data is deleted every night according to the retention settings. |
| How it works | Nightly job at 02:30 PKT: drops expired metric shards (whole tables), deletes old events, audit entries, closed alerts and expired sessions. |
| What you see | Settings → General → Retention. |
| Alerts | — |
| Configuration | Raw 7 days (1–60), 5-minute 90 days (7–730), hourly 730 days (30–3650), events 365 days, audit 365 days, closed alerts 365 days. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | 🟡 Limited — Whole day/month/year shards are dropped, so data can be kept up to one shard period longer than set (hourly data up to one year longer). |

#### PLT-09 · Logging

| Field | Content |
|---|---|
| What it does | The service writes a daily log file of its own activity; secrets are never logged. |
| How it works | Serilog, `C:\ProgramData\NetWatch\logs\netwatch-yyyyMMdd.log`, daily roll-over. |
| What you see | Log files; included in the diagnostic bundle (24 h). |
| Alerts | — |
| Configuration | `Logging:MinimumLevel` (Information), `Logging:RetainedDays` (30) in appsettings.json. |
| Requirements | — |
| Permissions | Server administrators (folder ACL). |
| Status | ✅ Available |

#### PLT-10 · Self-monitoring

| Field | Content |
|---|---|
| What it does | NetWatch checks its own health and raises an alert when a check fails. It creates a device for itself (“<server> (NetWatch)”). |
| How it works | Ten checks: Data disk, Polling, Metric storage, Alert engine, Syslog/trap intake, Web certificate, Backups, Scheduled reports, Notifications, Event forwarding (only when WEF is used). |
| What you see | Settings → System status → self-checks; self device on Devices. |
| Alerts | A-61 “NetWatch self-check failing” (Major, after 2 min). |
| Configuration | Settings → System: free disk < 10 % or < 5 GB; certificate warning 30 days; max backup age 36 h. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### PLT-11 · Offline / air-gap design

| Field | Content |
|---|---|
| What it does | NetWatch never contacts the internet: no CDN, web fonts, telemetry, update checks or online certificate revocation. |
| How it works | UI build fails on any external URL (`check-offline.mjs`); vendor OIDs are built in (no MIB files); outgoing TLS uses revocation *NoCheck* or *Offline* only, certificate downloads disabled, 2 s retrieval timeout; alert sounds are synthesised in the browser. |
| What you see | Settings → General → Security → Certificate revocation. |
| Alerts | — |
| Configuration | Certificate revocation: NoCheck (default) or Offline (local CRL cache). |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available — QA measured 0 non-loopback TCP connections in 60 min; DNS/CRL attempts were not audited (needs admin rights, pilot). |

#### PLT-12 · Local time zone

| Field | Content |
|---|---|
| What it does | All schedules and displayed times use Pakistan Standard Time. |
| How it works | Fixed zone Asia/Karachi (UTC+5, no DST); data stored in UTC. |
| What you see | All timestamps (“PKT”). |
| Alerts | Working hours for A-44 are in PKT. |
| Configuration | Not configurable. |
| Requirements | — |
| Permissions | — |
| Status | 🟡 Limited — Time zone is fixed to PKT; it cannot be changed in settings. |

## 2. Security

#### SEC-01 · Local user sign-in

| Field | Content |
|---|---|
| What it does | Users sign in with a NetWatch user name and password. |
| How it works | POST `/api/auth/login`; password hashes PBKDF2-HMAC-SHA512, 210,000 iterations (ASP.NET Identity v3 format); constant-time check for unknown users. |
| What you see | Login page; Settings → Users. |
| Alerts | — |
| Configuration | Settings → Users: create, edit, disable, delete, reset password, unlock. |
| Requirements | — |
| Permissions | Manage users: Admin. |
| Status | ✅ Available |

#### SEC-02 · First-start admin account

| Field | Content |
|---|---|
| What it does | On first start NetWatch creates user `admin` with a random one-time password that must be changed at first sign-in. |
| How it works | Password written to `C:\ProgramData\NetWatch\initial-admin-password.txt` (Administrators only); until changed, only `/api/auth/*` is allowed. `--reset-admin` creates a new one-time password. |
| What you see | Login → forced password change. |
| Alerts | — |
| Configuration | — |
| Requirements | Delete the file after first sign-in. |
| Permissions | — |
| Status | ✅ Available |

#### SEC-03 · Roles

| Field | Content |
|---|---|
| What it does | Three roles, each including the rights of the one below: Viewer (read), Operator (work alerts, poll now, report schedules), Admin (everything). |
| How it works | Role claims checked per endpoint; Settings page only rendered for Admin. |
| What you see | Settings → Users; top bar shows the role. |
| Alerts | — |
| Configuration | Role per user; AD group → role mapping (SEC-04). |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available — Executed permission matrix: 137 endpoints × anonymous/kiosk/Viewer/Operator/Admin, no violation. |

#### SEC-04 · Active Directory (LDAP) sign-in

| Field | Content |
|---|---|
| What it does | Domain users sign in with their AD account; their role comes from AD group membership. |
| How it works | LDAPS 636 (default), StartTLS or plain 389 with Negotiate (Kerberos/NTLM signing+sealing) or simple bind (TLS only); nested groups via `LDAP_MATCHING_RULE_IN_CHAIN` (1.2.840.113556.1.4.1941); highest mapped role wins; users in no mapped group are refused. |
| What you see | Settings → Directory (AD), with a test button. |
| Alerts | — |
| Configuration | Enabled (off), servers (up to 10, tried in order), port 636, security Ldaps, bind Negotiate, validate certificate (on), domain, base DN, timeout 10 s, group DN → role mappings. |
| Requirements | TCP 636 (or 389) to the DCs; DC certificate trusted by the NetWatch server for LDAPS. |
| Permissions | Admin |
| Status | 🧪 Pending pilot — Tested with unit tests only; not yet against a real domain. |

#### SEC-05 · Password policy

| Field | Content |
|---|---|
| What it does | Local passwords must be long and mixed. |
| How it works | Minimum 10 characters (8–128), at least 3 of lower/upper/digit/symbol, max 256, must not contain the user name. |
| What you see | Account page; Users tab. |
| Alerts | — |
| Configuration | Settings → General → Security → minimum length (10). |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### SEC-06 · Account lockout and login rate limit

| Field | Content |
|---|---|
| What it does | Repeated wrong passwords lock the account for a while; sign-in attempts per client are rate-limited. |
| How it works | Lock after 5 failures for 15 min (HTTP 423 with minutes left); admin can unlock. Sign-in and kiosk sign-in: 10 requests/min per client; heavy endpoints (reports, backups, diagnostics): 10/min (HTTP 429). |
| What you see | Login message; Users tab (Unlock). |
| Alerts | Audit entries LoginFailed / LoginBlockedLocked. |
| Configuration | Max failed logins 5 (3–50), lockout 15 min (1–1440). |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### SEC-07 · Sessions

| Field | Content |
|---|---|
| What it does | Sign-in creates a server-side session that ends after inactivity or a maximum lifetime and can be revoked. |
| How it works | Cookie `nw_session`: HttpOnly, Secure, SameSite=Strict; token stored as SHA-256 hash; idle timeout 30 min, absolute 12 h; revoked on logout, user disabled or role change. |
| What you see | — |
| Alerts | — |
| Configuration | Idle timeout 30 min (5–1440), absolute 12 h (1–168). |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### SEC-08 · Audit trail

| Field | Content |
|---|---|
| What it does | Every sign-in (also failed) and every change made by a user is recorded with who, when, from where and what. |
| How it works | Audit table in netwatch.db; written by all write endpoints and by DC CSV exports. |
| What you see | Settings → Audit trail. |
| Alerts | — |
| Configuration | Audit retention 365 days (30–3650). |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### SEC-09 · Encrypted credentials (DPAPI)

| Field | Content |
|---|---|
| What it does | Passwords, communities and keys of monitored systems are stored encrypted and never returned by the API. |
| How it works | Windows DPAPI, machine scope, for credential profiles, SMTP password and PFX passwords. Only a non-secret hint (e.g. user name) is shown. |
| What you see | Settings → Credentials. |
| Alerts | — |
| Configuration | — |
| Requirements | Secrets are tied to the server; use a portable backup to move them (BAK-03). |
| Permissions | Admin |
| Status | ✅ Available |

#### SEC-10 · HTTPS certificate handling

| Field | Content |
|---|---|
| What it does | NetWatch makes its own certificate on first start and lets you replace it with one from your internal CA. |
| How it works | Order: (1) `Https:CertificateThumbprint` in LocalMachine\My, (2) `Https:CertificatePath` .pfx with DPAPI-protected password (`--set-certificate`), (3) PFX imported in the console (`certs\console.pfx`), (4) self-signed RSA 3072 / SHA-256, valid 825 days, SAN = host name, FQDN, localhost, local IPv4s, renewed 30 days before expiry. Import checks private key, validity, server-auth EKU, RSA ≥ 2048, host names. |
| What you see | Settings → System → Certificate (upload / remove). |
| Alerts | “Web certificate” self-check, 30 days. |
| Configuration | appsettings.json `Https:*`; console upload. |
| Requirements | Distribute `certs\netwatch-selfsigned.cer` or use an internal-CA certificate so browsers trust it. |
| Permissions | Admin |
| Status | ✅ Available |

#### SEC-11 · Security headers and CSRF protection

| Field | Content |
|---|---|
| What it does | Browser-side protections against script injection, framing and cross-site requests. |
| How it works | CSP (`default-src 'self'`, `frame-ancestors 'none'` …), `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, Permissions-Policy, COOP/CORP same-origin, HSTS 1 year, `Cache-Control: no-store` on the API; custom header `X-NetWatch-Request` required on writes. |
| What you see | — |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | — |
| Status | ✅ Available |

#### SEC-12 · NOC kiosk displays

| Field | Content |
|---|---|
| What it does | A wall screen can be signed in with a one-time link as a read-only account that does not time out. |
| How it works | Admin creates a kiosk link; it signs in a hidden Viewer account (auth source Kiosk) with a persistent cookie (1 year) and no idle timeout; revocable at once. |
| What you see | Settings → General → NOC displays; `/noc`. |
| Alerts | — |
| Configuration | Create / revoke per display. |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### SEC-13 · Outgoing TLS policy and certificate pinning

| Field | Content |
|---|---|
| What it does | Connections to vCenter, iLO/iDRAC, LDAPS and SMTP never try online revocation; self-signed vCenter/iLO/iDRAC certificates are pinned on first use. |
| How it works | Shared `TlsPolicy`; SHA-256 thumbprint pinned per device (trust on first use), a different certificate is refused until an admin resets it. SSH host keys are pinned the same way. |
| What you see | Device detail → reset thumbprint / host key. |
| Alerts | Collection-failure alerts when a pin no longer matches. |
| Configuration | Trust on first use: VMware (on), hardware (on). |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

## 3. Device inventory

#### INV-01 · Manual add / edit / delete

| Field | Content |
|---|---|
| What it does | Add a device by name and IP address and choose its type, group, credentials, poll interval and upstream device. |
| How it works | Device types: Switch, Router, Firewall, Server, VM, DomainController, Hypervisor, VCenter, Other. Validated input; inventory change re-plans polling at once. |
| What you see | Devices → Add device; device detail → Edit. |
| Alerts | — |
| Configuration | Name, IP, host name, type, group, tags, poll interval (≥ 15 s, default 60 s), enabled, parent device, notes, credentials per purpose (SNMP, Management, vCenter, BMC), BMC address. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### INV-02 · CSV import

| Field | Content |
|---|---|
| What it does | Import many devices from a spreadsheet; duplicates are skipped and errors listed per line. |
| How it works | Upload ≤ 5 MB; header names case-insensitive; required `name`, `ip` (or `ip_address`); optional `type`, `location`, `group`, `tags`, `snmp_credential`, `poll_interval`, `parent`, `notes`. Template download. |
| What you see | Devices → Import. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available — No dry-run: valid rows are created immediately (see G). |

#### INV-03 · Subnet discovery

| Field | Content |
|---|---|
| What it does | Scan a subnet with ping and SNMP, review what was found, and add the selected devices. |
| How it works | IPv4 CIDR up to /22 (1,024 hosts), 64 probes in parallel; ping (1 × 800 ms) then SNMP with each chosen v2c/v3 credential (1.5 s); reads sysName/sysDescr/sysObjectID, suggests vendor and type, marks addresses already monitored. Nothing is added until you pick rows (review step = dry run). |
| What you see | Devices → Discover. |
| Alerts | — |
| Configuration | Subnet, SNMP credentials to try. |
| Requirements | ICMP and UDP 161 to the subnet. |
| Permissions | Admin |
| Status | ✅ Available — Verified here on 127.0.0.1/32 (scan, “already monitored” flag); SNMP answers from real devices: pilot. |

#### INV-04 · Groups and sites

| Field | Content |
|---|---|
| What it does | Organise devices in a tree such as Site › Building › Floor › Rack, or logical groups. |
| How it works | Group kinds Site, Building, Floor, Rack, Logical; rules, maintenance windows and reports can target a group including sub-groups. |
| What you see | Settings → Groups & sites; Overview group tiles; NOC tiles. |
| Alerts | Rule scope by group. |
| Configuration | Create, rename, move, delete groups. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### INV-05 · Tags

| Field | Content |
|---|---|
| What it does | Free-text labels on devices for filtering and for rule/maintenance scope. |
| How it works | Many tags per device; list of used tags via API. |
| What you see | Devices filter; device form. |
| Alerts | Rule scope by tag. |
| Configuration | Per device. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### INV-06 · Device maintenance mode

| Field | Content |
|---|---|
| What it does | Put one or many devices in maintenance (optionally until a time): polling continues, new alerts are not raised. |
| How it works | Device status *Maintenance*; bulk action. |
| What you see | Devices → select → Maintenance; status badge. |
| Alerts | Suppresses new alerts for those devices. |
| Configuration | On/off, until (optional). |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### INV-07 · Credential profiles

| Field | Content |
|---|---|
| What it does | Reusable, encrypted sets of credentials assigned to many devices. |
| How it works | Kinds: SNMP v2c (community), SNMP v3 (user, auth MD5/SHA-1/SHA-256/384/512, priv DES/AES-128/192/256), Windows (domain\user + password), SSH (user + password or key), vCenter, Redfish (iLO/iDRAC). DPAPI-encrypted (SEC-09). Warning shown for v2c (clear text). |
| What you see | Settings → Credentials. |
| Alerts | — |
| Configuration | Create, edit, delete, test. |
| Requirements | Read-only accounts on the devices. |
| Permissions | Admin |
| Status | ✅ Available |

#### INV-08 · Upstream (parent) device

| Field | Content |
|---|---|
| What it does | Record which device a device depends on (e.g. its access switch) so alerts behind a failed device are suppressed. |
| How it works | `ParentDeviceId`; used by dependency suppression (ALR-11) and the topology map. |
| What you see | Device form; Topology. |
| Alerts | ALR-11. |
| Configuration | Per device or from LLDP/CDP suggestions (SW-27). |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### INV-09 · Poll now

| Field | Content |
|---|---|
| What it does | Trigger an immediate poll of a device. |
| How it works | POST `/api/devices/{id}/poll-now`. |
| What you see | Device detail → Poll now. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Operator, Admin |
| Status | ✅ Available |

#### INV-10 · Automatic inventory data

| Field | Content |
|---|---|
| What it does | Vendor, model, serial number, firmware, sysName, sysDescr and location are filled in from the device. |
| How it works | SNMPv2-MIB system group and ENTITY-MIB chassis entry (entPhysicalModelName/SerialNum/SoftwareRev/FirmwareRev); firmware also parsed from sysDescr (Cisco, HP/Aruba, Junos); vendor from the sysObjectID enterprise number (Cisco, HP, HPE Aruba, Aruba, Juniper, Huawei, H3C, Alcatel-Lucent, Fortinet, Palo Alto, Microsoft, Net-SNMP, VMware, Dell, HPE, IBM). |
| What you see | Device detail; Inventory report. |
| Alerts | — |
| Configuration | Inventory refresh 24 h. |
| Requirements | SNMP read access. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Verified with the simulator only. |

## 4. Switch monitoring

#### SW-01 · Availability (ping)

| Field | Content |
|---|---|
| What it does | Checks every device with ICMP echo and measures latency and packet loss; decides Up / Warning / Down. |
| How it works | ICMP 3 echoes × 1000 ms timeout per poll. Up = ping or SNMP answers (servers: also WinRM/SSH). Warning = SNMP fails while ping works, ≥ 20 % loss, or server collection fails. Down after 3 consecutive failed polls. |
| What you see | Status badges everywhere; device detail RTT and loss graphs. |
| Alerts | A-01 Device down (Critical), A-02 latency > 100 ms 5 min, A-03 loss > 20 % 3 min. |
| Configuration | Ping count 3 (1–10), timeout 1000 ms (100–5000), down after 3 polls (1–20). |
| Requirements | ICMP echo allowed from the NetWatch server. |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### SW-02 · SNMP v2c polling

| Field | Content |
|---|---|
| What it does | Reads switch data with a read-only community. |
| How it works | UDP 161, GetBulk (25 repetitions); timeout 3000 ms, 1 retry. |
| What you see | Device detail. |
| Alerts | A-12 SNMP not responding (3 min). |
| Configuration | SNMP timeout 3000 ms (500–15000), retries 1 (0–5); credential per device. |
| Requirements | Read-only community; ACL allowing the NetWatch server. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Community travels in clear text (use v3 where possible). SNMP v1 polling is not supported. |

#### SW-03 · SNMP v3 polling

| Field | Content |
|---|---|
| What it does | Reads switch data with an authenticated and encrypted SNMPv3 user. |
| How it works | USM: auth MD5, SHA-1, SHA-256, SHA-384, SHA-512; privacy DES, AES-128, AES-192, AES-256; noAuthNoPriv/authNoPriv/authPriv. |
| What you see | Device detail. |
| Alerts | A-12. |
| Configuration | SNMP v3 credential profile. |
| Requirements | SNMPv3 user with read view; authPriv with SHA-256+/AES recommended. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Verified with simulator and connection test only. |

#### SW-04 · Port status

| Field | Content |
|---|---|
| What it does | Shows each port’s administrative and operational state and when it last changed. |
| How it works | IF-MIB ifAdminStatus, ifOperStatus, ifLastChange; names from ifName/ifDescr/ifAlias, type ifType, MAC ifPhysAddress. |
| What you see | Switches port grid; device detail interface table. |
| Alerts | A-06 Important port down (admin-up and oper not up/dormant). |
| Configuration | Per interface: monitored, important, expected speed. |
| Requirements | SNMP read of IF-MIB. |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SW-05 · Port speed

| Field | Content |
|---|---|
| What it does | Shows the negotiated speed of each port. |
| How it works | IF-MIB ifHighSpeed (Mbit/s) with fallback to ifSpeed; re-read every 30 min and after an important port comes up. |
| What you see | Port grid, interface table. |
| Alerts | A-10 (SW-24). |
| Configuration | Interface refresh 30 min. |
| Requirements | IF-MIB ifXTable. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SW-06 · Duplex

| Field | Content |
|---|---|
| What it does | Shows whether each port runs half or full duplex. |
| How it works | EtherLike-MIB dot3StatsDuplexStatus (1 unknown, 2 half, 3 full). |
| What you see | Interface table. |
| Alerts | A-09 Important port half duplex (Major). |
| Configuration | — |
| Requirements | EtherLike-MIB support on the switch. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Only alerts on important ports; simulator only. |

#### SW-07 · Traffic

| Field | Content |
|---|---|
| What it does | In and out bits per second for every monitored port. |
| How it works | IF-MIB ifHCInOctets/ifHCOutOctets (64-bit), fallback ifInOctets/ifOutOctets (32-bit) with counter-wrap and reboot handling. |
| What you see | Device detail → interface graph (In/Out); Overview top interfaces. |
| Alerts | — |
| Configuration | Per interface: monitored (unmonitored ports are not stored). |
| Requirements | IF-MIB. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SW-08 · Utilization

| Field | Content |
|---|---|
| What it does | Percentage of port speed used, in and out. |
| How it works | Rate ÷ speed; the alert uses the larger of in/out. |
| What you see | Interface graph; Interface utilization report. |
| Alerts | A-07 High interface utilization > 90 % for 5 min (important ports). |
| Configuration | Rule threshold/duration. |
| Requirements | IF-MIB. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SW-09 · Errors

| Field | Content |
|---|---|
| What it does | In and out errors per second per port. |
| How it works | IF-MIB ifInErrors / ifOutErrors. |
| What you see | Interface graph (In/Out errors/s). |
| Alerts | No default rule; metric `iface.errors` available for custom threshold rules. |
| Configuration | — |
| Requirements | IF-MIB. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SW-10 · Discards

| Field | Content |
|---|---|
| What it does | In and out discards per second per port. |
| How it works | IF-MIB ifInDiscards / ifOutDiscards. |
| What you see | Interface graph (discards/s). |
| Alerts | None (discards are stored and graphed but have no metric key for rules). |
| Configuration | — |
| Requirements | IF-MIB. |
| Permissions | Viewer, Operator, Admin |
| Status | 🟡 Limited — Graph only; no alert rule can be built on discards. Simulator only. |

#### SW-11 · Health – Cisco

| Field | Content |
|---|---|
| What it does | CPU, memory, temperatures, fans and power supplies of Cisco devices. |
| How it works | CPU: CISCO-PROCESS-MIB cpmCPUTotal5minRev (avg); memory: cpmCPUMemoryUsed/Free, fallback CISCO-MEMORY-POOL-MIB ciscoMemoryPoolUsed/Free; CISCO-ENVMON-MIB temperature (descr, value, state), fan state, PSU state (1 normal, 2 warning, 3/4/6 critical, 5 not present). |
| What you see | Device detail → CPU/memory/temperature graphs, sensors table. |
| Alerts | A-04 CPU > 90 %, A-05 memory > 90 % (5 min), A-11 sensor problem. |
| Configuration | — |
| Requirements | SNMP read of those MIBs. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator and snmpwalk samples only. |

#### SW-12 · Health – HP ProCurve / ArubaOS-Switch

| Field | Content |
|---|---|
| What it does | CPU, memory and chassis sensors of HP/Aruba (ProCurve) switches. |
| How it works | STATISTICS-MIB hpSwitchCpuStat (1.3.6.1.4.1.11.2.14.11.5.1.9.6.1.0); NETSWITCH-MIB memory total/free; HP-ICF-CHASSIS hpicfSensorStatus/Descr (fan/power/temperature by name; 2 bad, 3 warning, 4 good). |
| What you see | Device detail. |
| Alerts | A-04, A-05, A-11. |
| Configuration | — |
| Requirements | SNMP read. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Temperatures are status only (no °C value) unless ENTITY-SENSOR-MIB provides them. |

#### SW-13 · Health – Juniper

| Field | Content |
|---|---|
| What it does | CPU, memory, temperatures, fans and power supplies of Juniper devices. |
| How it works | JUNIPER-MIB jnxOperatingTable: descr, state, temp, CPU, buffer (memory); Routing Engine rows (or FPC rows) give CPU/memory; up to 12 temperatures; fans and PEM/PSU states. |
| What you see | Device detail. |
| Alerts | A-04, A-05, A-11. |
| Configuration | — |
| Requirements | SNMP read. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator and samples only. |

#### SW-14 · Health – HPE Aruba (ArubaOS-CX)

| Field | Content |
|---|---|
| What it does | Fans, power supplies and temperatures of ArubaOS-CX switches from a built-in data-driven profile. |
| How it works | ARUBAWIRED tables under 1.3.6.1.4.1.47196.4.1.1.3.11 (PSU .2, temperature .3 in millidegrees, fan .5). CPU/memory from the standard HOST-RESOURCES fallback. |
| What you see | Device detail. |
| Alerts | A-11, plus A-04/A-05 if HOST-RESOURCES answers. |
| Configuration | Can be corrected in `snmp-profiles.json` (SW-16). |
| Requirements | SNMP read. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Profile marked Verified = false: OIDs not yet checked against a real ArubaOS-CX switch. |

#### SW-15 · Health – other vendors (standard MIBs)

| Field | Content |
|---|---|
| What it does | For any vendor without its own code, CPU, memory and temperatures come from standard MIBs. |
| How it works | HOST-RESOURCES-MIB hrProcessorLoad (avg) and hrStorageRam; ENTITY-SENSOR-MIB celsius sensors (up to 12, with scale/precision). Also used to fill gaps for Cisco/HP/Juniper. |
| What you see | Device detail. |
| Alerts | A-04, A-05, A-11. |
| Configuration | — |
| Requirements | Agent must implement those MIBs. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Values depend on the agent; fans/PSUs are not read generically. |

#### SW-16 · Custom SNMP health profiles

| Field | Content |
|---|---|
| What it does | Add or correct vendor health tables without a new build. |
| How it works | `C:\ProgramData\NetWatch\snmp-profiles.json`: vendor (+ optional sysObjectID prefix), CPU column, memory % column, sensor tables (name/state/value columns, scale, unit, OK/warning/critical/not-present values). Re-read when the file changes; errors fall back to built-ins. |
| What you see | — |
| Alerts | A-11. |
| Configuration | JSON file in the data folder. |
| Requirements | — |
| Permissions | Server administrators |
| Status | ✅ Available — No editor in the UI. |

#### SW-17 · Reboot detection

| Field | Content |
|---|---|
| What it does | Detects that a device restarted. |
| How it works | sysUpTime lower than at the previous poll. |
| What you see | Device events. |
| Alerts | A-13 Device rebooted (Warning). |
| Configuration | — |
| Requirements | SNMP. |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### SW-18 · SNMP trap receiver

| Field | Content |
|---|---|
| What it does | Receives SNMP traps and informs from devices, names well-known traps, stores and searches them. |
| How it works | UDP **162**; v1, v2c, v3 traps (decrypted with the SNMPv3 users of the credential profiles, refreshed every 5 min); answers informs; v1 agent-addr used for relayed traps. Built-in names/severities: coldStart, warmStart, linkDown/Up, authenticationFailure, egpNeighborLoss, BGP, ENTITY config change, STP newRoot/topologyChange, Cisco config/syslog/envmon (shutdown, voltage, temperature, fan, redundant supply), Juniper PSU/fan/temperature failure and OK. |
| What you see | Events → SNMP traps (search by device, severity, text, time). |
| Alerts | A-21 SNMP authentication failure; custom EventMatch rules on trap OID. |
| Configuration | Settings → General → Receivers: traps on, port 162, accept unknown sources (on), max 2,000 messages/s. |
| Requirements | Devices send traps to the NetWatch server; UDP 162 inbound. |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available — Verified by integration tests with real sockets. Port 162 must be free (no other trap service). |

#### SW-19 · Syslog receiver

| Field | Content |
|---|---|
| What it does | Receives syslog from devices, parses it, stores and searches it. |
| How it works | UDP **514** and TCP **514**; RFC 5424, RFC 3164 (BSD), Cisco sequence numbers and `%FAC-SEV-MNEMONIC`, HP/Aruba and Juniper variants; never fails on bad input; facility, severity, host, app, procid, msgid kept. |
| What you see | Events → Syslog (search). |
| Alerts | A-20 severity 0–2, A-22 ≥ 10 login-failure messages in 5 min; custom rules by pattern/source. |
| Configuration | Syslog UDP on/port 514, TCP on/port 514, accept unknown sources, max 2,000 messages/s (shared with traps). |
| Requirements | Devices log to the NetWatch server; UDP/TCP 514 inbound. |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available — Verified with simulator messages and real-socket tests. |

#### SW-20 · Important ports

| Field | Content |
|---|---|
| What it does | Mark uplinks and server ports as important; only they raise port-down, duplex, speed and utilization alerts. |
| How it works | Per-interface flag; bulk edit; rule scope “important interfaces only”. |
| What you see | Device detail interface table; port grid highlight. |
| Alerts | A-06, A-07, A-09, A-10. |
| Configuration | Per interface or bulk. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### SW-21 · Link flap detection

| Field | Content |
|---|---|
| What it does | Detects ports that go up and down repeatedly. |
| How it works | A status change between polls counts 1; a moved ifLastChange with the same status counts 2 (down and up between polls); count over the flap window and over 24 h. |
| What you see | Interface table (flaps in 24 h). |
| Alerts | A-08 Port flapping (Warning). |
| Configuration | Flap changes 4 (2–100) within 10 min (2–240). |
| Requirements | IF-MIB ifLastChange recommended. |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SW-22 · Expected speed check

| Field | Content |
|---|---|
| What it does | Alerts when an important port negotiated below the speed it should run at (e.g. 10G uplink at 1G). |
| How it works | Compare current speed with the per-port expected speed while the port is up. |
| What you see | Interface edit (expected speed). |
| Alerts | A-10 Important port below expected speed (Warning). |
| Configuration | Expected speed per interface (empty = no check). |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SW-23 · LLDP / CDP neighbours

| Field | Content |
|---|---|
| What it does | Learns which device and port is connected to each switch port. |
| How it works | LLDP-MIB lldpRemTable/lldpLocPortTable/lldpRemManAddrTable and CISCO-CDP-MIB cdpCacheTable, read with the interface table; duplicates (LLDP+CDP) merged; neighbours matched to monitored devices by IP, then name. |
| What you see | Topology → links; device detail neighbours. |
| Alerts | — |
| Configuration | Settings → General → Monitoring → discover neighbours (on). |
| Requirements | LLDP or CDP enabled on the switches. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator and snmpwalk samples only. |

#### SW-24 · Topology map

| Field | Content |
|---|---|
| What it does | Shows devices as a tree by upstream device, coloured by status; collapsed branches still show trouble inside. |
| How it works | Built from parent devices and neighbour links. |
| What you see | Topology page. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### SW-25 · Dependency suggestions

| Field | Content |
|---|---|
| What it does | Suggests upstream (parent) devices from LLDP/CDP links, which an admin can apply in bulk. |
| How it works | `/api/topology/suggestions` and `/apply`. |
| What you see | Topology → suggestions. |
| Alerts | Feeds ALR-11. |
| Configuration | — |
| Requirements | SW-23. |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | 🧪 Pending pilot — Simulator returned no suggestions in this run (parents already set by the seeder). |

#### SW-26 · Port grid

| Field | Content |
|---|---|
| What it does | A live, colour-coded grid of all ports of a switch. |
| How it works | Live interface state from SignalR and the API. |
| What you see | Switches page. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### SW-27 · Interface and device graphs

| Field | Content |
|---|---|
| What it does | Time graphs of traffic, errors, discards, CPU, memory, temperature, latency, loss and availability. |
| How it works | ECharts; ranges 1 h, 24 h, 7 d, 30 d or custom; peaks shown. |
| What you see | Device detail. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

## 5. Server monitoring

#### SRV-01 · Windows collection over WinRM (WMI)

| Field | Content |
|---|---|
| What it does | Collects Windows server health without an agent. |
| How it works | WMI over WS-Management (WinRM client, Kerberos/NTLM) on TCP 5985 (or HTTPS 5986): Win32_OperatingSystem, Win32_ComputerSystem, Win32_PerfFormattedData_PerfOS_Processor (_Total), Win32_LogicalDisk (DriveType=3), Win32_PerfFormattedData_Tcpip_NetworkInterface, Win32_Service, Win32_QuickFixEngineering. Each query fails independently. |
| What you see | Servers page; device detail → Server panel. |
| Alerts | A-17 Server monitoring failing (5 min) when ping works but collection fails. |
| Configuration | Settings → General → Servers: WinRM port 5985, WinRM HTTPS (off), timeout 20 s. |
| Requirements | WinRM enabled; account in Remote Management Users with WMI Remote Enable on root\cimv2 (and root\default for registry). |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SRV-02 · DCOM fallback

| Field | Content |
|---|---|
| What it does | Uses classic WMI over DCOM when WinRM is not reachable. |
| How it works | System.Management, PacketPrivacy, impersonate. |
| What you see | Server panel shows method WinRm/Dcom. |
| Alerts | A-17. |
| Configuration | DCOM fallback (on). |
| Requirements | TCP 135 + dynamic RPC; Distributed COM Users. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SRV-03 · CPU and memory (servers)

| Field | Content |
|---|---|
| What it does | CPU % and memory % of servers. |
| How it works | Windows: PerfOS processor and Win32_OperatingSystem; Linux: /proc/stat (1 s sample) and /proc/meminfo; SNMP: HOST-RESOURCES. |
| What you see | Servers list; graphs. |
| Alerts | A-04, A-05. |
| Configuration | — |
| Requirements | See SRV-01/SRV-08. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SRV-04 · Disk volumes

| Field | Content |
|---|---|
| What it does | Used % and free GB of each fixed volume or mount point. |
| How it works | Windows Win32_LogicalDisk; Linux `df -PkT` (tmpfs, devtmpfs, squashfs, overlay, efivarfs excluded); SNMP hrStorageTable. |
| What you see | Server panel; Server capacity report. |
| Alerts | A-14 > 85 % (2 min), A-15 > 95 % (2 min); `disk.free.gb` for custom rules. |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SRV-05 · Network throughput (servers)

| Field | Content |
|---|---|
| What it does | Total in/out bits per second of all adapters. |
| How it works | Windows Tcpip_NetworkInterface; Linux /proc/net/dev. |
| What you see | Server graphs. |
| Alerts | None by default (metrics `net.in.bps`, `net.out.bps`). |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SRV-06 · Watched services

| Field | Content |
|---|---|
| What it does | Alerts when a chosen Windows service or systemd unit is not running. |
| How it works | Windows Win32_Service; Linux `systemctl show` (validated unit names). Domain controllers get NTDS, DNS, Netlogon, Kdc, DFSR, W32Time by default. |
| What you see | Server panel → services (edit list). |
| Alerts | A-16 Service not running (Major, 1 min). |
| Configuration | Service list per server. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SRV-07 · Windows event logs

| Field | Content |
|---|---|
| What it does | Collects Critical and Error entries (optionally Warnings) from the System and Application logs. |
| How it works | Remote EventLogSession (RPC) with XPath by level; bookmark per log so nothing is read twice; max 200 entries per log and poll. |
| What you see | Events → Windows event logs; device detail. |
| Alerts | A-19 Windows critical event (Major); custom EventMatch by event ID/provider. |
| Configuration | Read event logs (on), include warnings (off), max 200 per poll (10–5000). |
| Requirements | Event Log Readers; firewall “Remote Event Log Management”. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SRV-08 · Pending reboot, last boot, last patch

| Field | Content |
|---|---|
| What it does | Shows when the server last booted, whether a reboot is pending, and the newest installed update. |
| How it works | Pending reboot: registry keys CBS `RebootPending` and WindowsUpdate `RebootRequired` via StdRegProv; boot time from Win32_OperatingSystem; last patch from Win32_QuickFixEngineering InstalledOn. |
| What you see | Servers list; Server panel. |
| Alerts | State `server.pending.reboot` is published; no default rule. |
| Configuration | — |
| Requirements | WMI root\default access for StdRegProv. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — No built-in rule for pending reboot (state key available for a custom rule). Simulator only. |

#### SRV-09 · Linux collection over SSH

| Field | Content |
|---|---|
| What it does | Collects Linux server health without an agent using one read-only command. |
| How it works | SSH TCP 22 (SSH.NET); reads /proc/stat, /proc/meminfo, /proc/uptime, /proc/loadavg, nproc, df, /proc/net/dev, `systemctl show` for watched units, optional lspci + nvidia-smi. Host key pinned on first use. |
| What you see | Servers page; Server panel. |
| Alerts | A-17, A-14/15, A-16, A-18. |
| Configuration | SSH port 22, timeout 20 s. |
| Requirements | SSH account (no root needed). |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator and parser tests only. |

#### SRV-10 · Linux load average

| Field | Content |
|---|---|
| What it does | Five-minute load average divided by CPU count. |
| How it works | /proc/loadavg ÷ nproc. |
| What you see | Server graphs. |
| Alerts | A-18 High load > 2 per CPU for 10 min (clears 1.5). |
| Configuration | — |
| Requirements | SSH. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### SRV-11 · Servers over SNMP only

| Field | Content |
|---|---|
| What it does | Servers without WinRM/SSH can still report CPU, memory and disks via SNMP. |
| How it works | HOST-RESOURCES-MIB hrProcessorLoad, hrStorageRam, hrStorageTable volumes. |
| What you see | Server panel (method Snmp). |
| Alerts | A-04, A-05, A-14, A-15. |
| Configuration | SNMP credential, no management credential. |
| Requirements | SNMP agent with HOST-RESOURCES-MIB. |
| Permissions | Viewer, Operator, Admin |
| Status | 🟡 Limited — No services, event logs, patches or load over SNMP. Simulator only. |

#### SRV-12 · GPU monitoring – Windows

| Field | Content |
|---|---|
| What it does | Lists GPUs and shows utilization and memory use; NVIDIA cards also temperature, power and fan. |
| How it works | Win32_VideoController (NVIDIA 10DE, AMD 1002, Intel data-centre/Arc), GPUPerformanceCounters GPUEngine and GPUAdapterMemory (Server 2019 / WDDM 2.x+), `nvidia-smi --query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,fan.speed,driver_version` over WinRM. |
| What you see | Device detail → GPU panel. |
| Alerts | A-55 util > 95 % 10 min, A-56 temperature > 85 °C 2 min, A-57 memory > 90 % 5 min, A-58 GPU missing/driver error. |
| Configuration | Fixed defaults: read GPUs (on), run nvidia-smi (on). These hardware settings exist in the code but have no UI or API to change them. |
| Requirements | WinRM remote shell rights for nvidia-smi. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Parser tests with samples only. GPU options cannot be changed (no settings UI/API). |

#### SRV-13 · GPU monitoring – Linux

| Field | Content |
|---|---|
| What it does | Lists GPUs with lspci and reads NVIDIA values with nvidia-smi. |
| How it works | Same SSH command (SRV-09). |
| What you see | GPU panel. |
| Alerts | A-55…A-58. |
| Configuration | Same as SRV-12. |
| Requirements | nvidia-smi installed for values. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — AMD/Intel values not read on Linux (listed only). |

#### SRV-14 · iLO / iDRAC hardware health (Redfish)

| Field | Content |
|---|---|
| What it does | Reads server hardware health from the management controller: processors, memory, fans, temperatures, power supplies, storage controllers and drives, and power use. |
| How it works | DMTF Redfish over HTTPS 443: /redfish/v1 → Systems (ProcessorSummary, MemorySummary, Storage + Drives), Chassis Thermal and Power (PowerConsumedWatts). Basic auth, certificate pinned on first use. Every 5 min. |
| What you see | Device detail → Hardware panel; power graph. |
| Alerts | A-59 component not OK, A-60 controller unreachable (15 min). |
| Configuration | Per server: BMC address + Redfish credential. Fixed defaults (no UI/API to change them): poll every 5 min, timeout 30 s, trust on first use on. |
| Requirements | Read-only BMC account (iLO Login privilege / iDRAC Read Only), Redfish enabled, TCP 443 from NetWatch. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Tested with HPE iLO 5/6 and Dell iDRAC 9 sample documents only. Interval/timeout/TOFU cannot be changed in the console. |

#### SRV-15 · Hardware health over SNMP (fallback)

| Field | Content |
|---|---|
| What it does | Without a Redfish account, gives the overall server health from the HPE or Dell SNMP agent. |
| How it works | HPE CPQHLTH overall condition, Dell iDRAC global system status. |
| What you see | Hardware panel (source SNMP). |
| Alerts | A-59, A-60. |
| Configuration | SNMP credential for the BMC/host. |
| Requirements | SNMP on iLO/iDRAC or the host agent. |
| Permissions | Viewer, Operator, Admin |
| Status | 🟡 Limited — Overall status only, no per-component detail. Not tested on real hardware. |

## 6. VMware monitoring

#### VMW-01 · vCenter / ESXi connection

| Field | Content |
|---|---|
| What it does | Reads the VMware inventory and health from vCenter or a standalone ESXi host with a read-only account. |
| How it works | vSphere Web Services (vim25 SOAP, API 7.0, works with 7.0 and 8.0) over HTTPS 443 `/sdk`; one property-collector query over a container view with paging; session cookie. |
| What you see | VMware page; vCenter device detail. |
| Alerts | A-23 vCenter/ESXi API not reachable (Major, 3 min). |
| Configuration | Settings → General → VMware: timeout 60 s, import events (on), import alarms (on), CPU ready (on), trust on first use (on), max 500 events per poll. |
| Requirements | Account with the Read-only role at vCenter root, propagated. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. A hung vCenter API stays “Up” (ICMP) until the 3-minute alert. |

#### VMW-02 · ESXi hosts

| Field | Content |
|---|---|
| What it does | Connection state, power, maintenance mode, CPU and memory use, hardware, version and boot time of each host. |
| How it works | HostSystem: runtime.connectionState/powerState/inMaintenanceMode/bootTime, summary.hardware.*, quickStats overallCpuUsage/MemoryUsage, product fullName. |
| What you see | VMware → Hosts & VMs; VMware report. |
| Alerts | A-24 host not connected (Critical), A-26 CPU > 90 %, A-27 memory > 90 % (5 min); state `vmware.host.maintenance` (no default rule). |
| Configuration | — |
| Requirements | VMW-01. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### VMW-03 · Host hardware sensors

| Field | Content |
|---|---|
| What it does | Alerts when an ESXi host hardware sensor is red. |
| How it works | runtime.healthSystemRuntime.systemHealthInfo.numericSensorInfo. |
| What you see | Hosts table. |
| Alerts | A-25 ESXi host hardware alarm (Major). |
| Configuration | — |
| Requirements | VMW-01. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### VMW-04 · Virtual machines

| Field | Content |
|---|---|
| What it does | Power state, guest OS, IP, host name, vCPU, memory, CPU/memory use, storage committed, host and datastores of each VM. |
| How it works | VirtualMachine properties (runtime, summary.config, quickStats, guest.*, storage, datastore); templates skipped. |
| What you see | VMware → Virtual machines; device detail of a VM (matched VM). |
| Alerts | — |
| Configuration | — |
| Requirements | VMW-01. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### VMW-05 · VM CPU ready and ballooning

| Field | Content |
|---|---|
| What it does | CPU ready % per vCPU and ballooned memory of powered-on VMs. |
| How it works | QueryPerf cpu.ready.summation (one query per poll); quickStats balloonedMemory. |
| What you see | VM table, graphs. |
| Alerts | A-33 CPU ready > 10 % for 10 min; balloon (`vmware.vm.balloon.mb`) has no default rule. |
| Configuration | Collect CPU ready (on). |
| Requirements | VMW-01. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### VMW-06 · VMware Tools

| Field | Content |
|---|---|
| What it does | Detects powered-on VMs whose VMware Tools are not running. |
| How it works | guest.toolsRunningStatus, toolsVersionStatus2. |
| What you see | VM table. |
| Alerts | A-34 VMware Tools not running (Warning, 15 min). |
| Configuration | — |
| Requirements | VMW-01. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### VMW-07 · Datastores

| Field | Content |
|---|---|
| What it does | Capacity, free space, type, accessibility and maintenance mode of each datastore. |
| How it works | Datastore summary.* properties. |
| What you see | VMware → Datastores; Server capacity and VMware reports. |
| Alerts | A-28 > 85 %, A-29 > 95 % used, A-30 not accessible (Critical). |
| Configuration | — |
| Requirements | VMW-01. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### VMW-08 · Snapshots

| Field | Content |
|---|---|
| What it does | Every VM snapshot with its age and approximate size. |
| How it works | snapshot tree and layoutEx.file/snapshot/disk. |
| What you see | VMware → Snapshots. |
| Alerts | A-31 older than 7 days, A-32 larger than 50 GB. |
| Configuration | — |
| Requirements | VMW-01. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Size is approximate (from file layout). Simulator only. |

#### VMW-09 · vCenter alarms

| Field | Content |
|---|---|
| What it does | Imports triggered vCenter alarms as NetWatch alerts and clears them when vCenter does. |
| How it works | triggeredAlarmState of the root folder plus Alarm info.name; acknowledged alarms clear the alert. |
| What you see | VMware → vCenter alarms; Alerts. |
| Alerts | A-35 red (Major), A-36 yellow (Warning). |
| Configuration | Import alarms (on). |
| Requirements | VMW-01. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### VMW-10 · vCenter events

| Field | Content |
|---|---|
| What it does | Imports vCenter events such as host connection lost, HA failover or VM powered off. |
| How it works | EventManager latestEvent / history collector; known event types get a severity (e.g. DasHostFailedEvent Critical, HostConnectionLostEvent Major, VmPoweredOffEvent Warning); EventEx uses its own severity; login/logout session events ignored. |
| What you see | VMware → vCenter events. |
| Alerts | A-37 vCenter major event (Major and above). |
| Configuration | Import events (on), max 500 per poll. |
| Requirements | VMW-01. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only (a storage bug found by the load test was fixed in 2.0). |

#### VMW-11 · VM → host → datastore view

| Field | Content |
|---|---|
| What it does | Hosts with their VMs and the datastores each VM uses, in one tree-style view. |
| How it works | Mapping from runtime.host and datastore references. |
| What you see | VMware → Hosts & VMs; VM panel on device detail. |
| Alerts | — |
| Configuration | — |
| Requirements | VMW-01. |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

## 7. Domain controller monitoring

#### DC-01 · Security log – remote reads

| Field | Content |
|---|---|
| What it does | Reads the Security log of each DC directly, without an agent. |
| How it works | Remote EventLogSession (RPC) with an XPath for the 26 event IDs (table 7.1) and `EventRecordID > bookmark`; first read starts at the newest record; max 5,000 events per poll. |
| What you see | Domain controllers page. |
| Alerts | Rules A-38…A-48. |
| Configuration | Settings → General → DC: read Security log (on), max 5,000 events/poll (100–50,000). |
| Requirements | Domain account in Event Log Readers; firewall “Remote Event Log Management”. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Parser tested with 26 sample exports; not yet with real DCs. |

#### DC-02 · Security log – Windows Event Forwarding

| Field | Content |
|---|---|
| What it does | Takes DC Security events from the NetWatch server’s ForwardedEvents log instead of reading each DC. |
| How it works | Background reader every 30 s; on start re-reads the last hour (duplicates skipped); events matched to the DC device by computer name (FQDN or short). |
| What you see | DC page shows WEF status and last read. |
| Alerts | “Event forwarding” self-check; rules A-38…A-48. |
| Configuration | Use forwarded events for all DCs (off) or list of DCs that forward. |
| Requirements | WEF subscription to the NetWatch server (Windows Event Collector), NETWORK SERVICE in Event Log Readers on the DCs. |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | 🧪 Pending pilot — Not yet tested with a real subscription. |

#### DC-03 · Mixed collection mode

| Field | Content |
|---|---|
| What it does | Some DCs can forward (WEF) while the others are read remotely. |
| How it works | `ForwardedDcs` list matched by name, short name or IP. |
| What you see | DC page. |
| Alerts | — |
| Configuration | Forwarded DCs list (up to 100). |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | 🧪 Pending pilot |

#### DC-04 · Event storage filters

| Field | Content |
|---|---|
| What it does | Keeps the database small by not storing routine successful logons. |
| How it works | 4624 stored only for logon types 2, 7, 10, 11 (default); computer accounts ($) skipped for 4624/4625/4768/4771/4776; SYSTEM/LOCAL SERVICE/NETWORK SERVICE/DWM-* skipped; ignore list of sources (IP, CIDR or workstation); successful 4624/4768/4776 stored but never evaluated by rules; duplicates (same DC + record ID) skipped. |
| What you see | — |
| Alerts | — |
| Configuration | Store successful logons (on), logon types [2,7,10,11], exclude computer accounts (on), ignore sources (up to 500). |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### DC-05 · DC security alert rules

| Field | Content |
|---|---|
| What it does | Built-in rules for password guessing, lockouts, privileged group changes, log clearing, audit policy changes, off-hours admin logons and account creation/deletion. |
| How it works | EventMatch and CountInWindow rules on `dc.security` with fields failed, privileged, offHours, targetUser, source, group, logonType (table A-38…A-48). |
| What you see | Alerts; Settings → Alert rules. |
| Alerts | A-38…A-48. |
| Configuration | Working days Mon–Fri, hours 08:00–17:00 PKT; privileged groups (Domain Admins, Enterprise Admins, Schema Admins, Administrators, Backup Operators, Account Operators). |
| Requirements | Audit policy (table C). |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available — Rules verified by unit tests and simulator; event input itself pending pilot. |

#### DC-06 · Replication status

| Field | Content |
|---|---|
| What it does | Inbound replication per partner and naming context: last success, last attempt, result and consecutive failures. |
| How it works | rootDSE + `msDS-NCReplInboundNeighbors` over signed and sealed LDAP (TCP 389), every 5 min. |
| What you see | DC page → Replication matrix. |
| Alerts | A-49 replication failing for 1 h, A-50 no success for longer than 3 h. |
| Configuration | Replication check every 5 min (1–1440), stale after 3 h (1–336). |
| Requirements | Any authenticated domain account can read this. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### DC-07 · Directory Service / DFS Replication errors

| Field | Content |
|---|---|
| What it does | Collects errors and warnings from the Directory Service and DFS Replication logs. |
| How it works | Remote event-log reads with bookmarks; DFS Replication log missing (FRS) is not an error. |
| What you see | Events → Windows event logs; DC page. |
| Alerts | A-52 for IDs 1311, 1864, 2042, 2087, 2088, 2213, 4012. |
| Configuration | — |
| Requirements | Event Log Readers. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### DC-08 · AD services

| Field | Content |
|---|---|
| What it does | Watches the core DC services. |
| How it works | Watched services NTDS, DNS, Netlogon, Kdc, DFSR, W32Time (SRV-06). |
| What you see | DC page → Overview. |
| Alerts | A-16. |
| Configuration | Service list per DC. |
| Requirements | WinRM/DCOM rights. |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### DC-09 · Time skew

| Field | Content |
|---|---|
| What it does | Measures each DC’s clock difference to the PDC emulator. |
| How it works | Win32_UTCTime over WinRM/DCOM on the DC and on the PDC, corrected to the query midpoint; PDC itself reports 0. |
| What you see | DC page; graph `dc.time.skew.sec`. |
| Alerts | A-53 > 30 s for 5 min (Warning), A-54 > 120 s (Critical). |
| Configuration | Checked with replication (5 min). |
| Requirements | WMI access to DCs and PDC. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### DC-10 · Service port reachability

| Field | Content |
|---|---|
| What it does | Checks that LDAP, Kerberos and DNS accept TCP connections on every poll. |
| How it works | TCP connect to 389, 88 and 53 (3 s timeout), with response time. |
| What you see | DC page → Overview (ports). |
| Alerts | A-51 DC service port unreachable (Major, 2 min). |
| Configuration | Check service ports (on). |
| Requirements | TCP 389/88/53 from NetWatch. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — TCP only (no LDAP bind, no DNS query). |

#### DC-11 · Domain, site and FSMO roles

| Field | Content |
|---|---|
| What it does | Shows domain, forest, site, global catalog and FSMO roles of each DC and who is PDC. |
| How it works | rootDSE and server objects over LDAP. |
| What you see | DC page → Overview. |
| Alerts | — |
| Configuration | — |
| Requirements | LDAP 389. |
| Permissions | Viewer, Operator, Admin |
| Status | 🧪 Pending pilot — Simulator only. |

#### DC-12 · Security event viewer

| Field | Content |
|---|---|
| What it does | Search and filter all stored DC Security events. |
| How it works | Filters: DC, event IDs, category, user, source (IP/workstation), group, free text, minimum severity, failed only, time range; quick presets (All, Failed logons, Successful logons, Lockouts/unlocks, Account changes, Group membership, Admin logons, Audit policy/log cleared, Directory changes); time presets 1 h, 24 h, 7 d, 30 d, all; paging; count capped at 10,000. |
| What you see | DC page → Security events. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### DC-13 · Saved searches

| Field | Content |
|---|---|
| What it does | Save a filter by name, privately or shared. |
| How it works | Saved per user and page; shared flag. |
| What you see | DC page → Security events. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin (own searches) |
| Status | ✅ Available |

#### DC-14 · CSV export of security events

| Field | Content |
|---|---|
| What it does | Export the filtered events to CSV. |
| How it works | Up to 100,000 rows, newest first; formula-injection safe; the export is audited. |
| What you see | DC page → Export. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### DC-15 · Account lockout tracker

| Field | Content |
|---|---|
| What it does | Lists lockouts with the calling computer, failures before the lockout, last failure source and when/who unlocked. |
| How it works | From 4740, 4767 and preceding failures; last 1,000. |
| What you see | DC page → Account lockouts. |
| Alerts | A-40. |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### DC-16 · Group change log

| Field | Content |
|---|---|
| What it does | All group membership changes with who changed what; filter privileged groups. |
| How it works | From 4728/4729/4732/4733/4756/4757; last 2,000. |
| What you see | DC page → Group changes. |
| Alerts | A-41. |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### DC-17 · Top failures

| Field | Content |
|---|---|
| What it does | Top 10 accounts and top 10 sources with failed logons. |
| How it works | Aggregation over the chosen hours. |
| What you see | DC page → Overview. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### DC-18 · Audit policy guidance

| Field | Content |
|---|---|
| What it does | Shows the advanced audit policy NetWatch needs on DCs. |
| How it works | Static table (same as table C). |
| What you see | DC page → Recommended audit policy. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

### 7.1 Every Security-log event ID handled

NetWatch reads exactly these 26 Security-log event IDs (`DcEventCatalog`). Default stored severity: 1102 and 4719 Critical; 4740 Major; additions to a privileged group Critical; 4720, 4726, 4741, 4743, 4728, 4732, 4756, 4724 Warning; any failure Warning; everything else Info. Fields common to all events: subject user/domain, target user/domain, source IP, workstation, logon type, Status and SubStatus where present. Every event also gets a one-line readable message.

| Event ID | Name | What it means | Fields extracted (besides time, DC, record ID) | Alerts by default |
|---|---|---|---|---|
| 4624 | Successful logon | A user signed in (interactive, unlock, RDP, cached by default). | Target user/domain, logon type, source IP, workstation | No (stored for investigation only) |
| 4625 | Failed logon | A sign-in failed (wrong password, unknown user, disabled, locked …). | Target user, source IP, workstation, logon type, Status/SubStatus (translated, e.g. 0xC000006A “wrong password”) | Yes, by count: A-38 (5 per account in 5 min), A-39 (20 per source in 5 min) |
| 4740 | Account locked out | An account was locked after too many bad passwords. | Target user, caller computer (where the bad passwords came from) | Yes: A-40 (Major) |
| 4767 | Account unlocked | A locked account was unlocked. | Target user, who unlocked | No (lockout tracker) |
| 4720 | User account created | A new user account was made. | New account, who created it | Yes: A-45 (Warning) |
| 4722 | User account enabled | A disabled account was enabled. | Account, who | No |
| 4725 | User account disabled | An account was disabled. | Account, who | No |
| 4726 | User account deleted | A user account was deleted. | Account, who | Yes: A-47 (Warning) |
| 4738 | User account changed | Properties of a user account changed. | Account, who | No |
| 4723 | Password change attempt | A user tried to change their own password. | Account, who | No |
| 4724 | Password reset attempt | Someone reset another account’s password. | Account, who | No (stored as Warning) |
| 4741 | Computer account created | A computer was joined / created in the domain. | Computer account, who | Yes: A-46 (Warning) |
| 4743 | Computer account deleted | A computer account was deleted. | Computer account, who | Yes: A-47 (Warning) |
| 4728 | Member added to global security group | An account was added to a global group. | Group, member (MemberName/SID), who | Yes if the group is privileged: A-41 (Critical) |
| 4729 | Member removed from global security group | An account was removed from a global group. | Group, member, who | No (group change log) |
| 4732 | Member added to local security group | An account was added to a domain-local group (e.g. Administrators). | Group, member, who | Yes if privileged: A-41 (Critical) |
| 4733 | Member removed from local security group | An account was removed from a domain-local group. | Group, member, who | No |
| 4756 | Member added to universal security group | An account was added to a universal group (e.g. Enterprise Admins). | Group, member, who | Yes if privileged: A-41 (Critical) |
| 4757 | Member removed from universal security group | An account was removed from a universal group. | Group, member, who | No |
| 4672 | Special privileges assigned to logon | An administrator-level logon (privileges such as SeDebugPrivilege). Computer accounts and service identities are not stored. | Subject user, first 8 privileges | Yes outside working hours only: A-44 (Warning) |
| 4768 | Kerberos TGT requested | A Kerberos ticket was requested; a non-success status means a failed logon. | Target user, source IP, Status | Failures only, by count (A-38, A-39); successes stored, not evaluated |
| 4771 | Kerberos pre-authentication failed | Kerberos logon failed, usually a wrong password (0x18). | Target user, source IP, Status | Yes by count: A-38, A-39, A-48 (50 per DC in 5 min) |
| 4776 | NTLM credential validation | The DC checked an NTLM password; non-success status = failure. | Target user, workstation, Status | Failures only, by count (A-38, A-39) |
| 4719 | Audit policy changed | The audit policy of the DC was changed. | Who, changes (AuditPolicyChanges) | Yes: A-43 (Critical) |
| 1102 | Security log cleared | Someone cleared the Security log. | Who (from UserData) | Yes: A-42 (Critical) |
| 5136 | Directory object modified | An AD object attribute changed (needs a SACL on the object, e.g. GPOs). | Object DN, attribute, set/removed, value (200 chars), who | No |

In addition, from the **Directory Service** and **DFS Replication** logs NetWatch stores errors and warnings and alerts (A-52) on event IDs **1311, 1864, 2042, 2087, 2088, 2213, 4012** (replication problems).

## 8. Alerting

#### ALR-01 · Threshold rules

| Field | Content |
|---|---|
| What it does | Alert when a metric is above/below a value for a time, with an optional clear level (hysteresis). |
| How it works | Operators > ≥ < ≤ = ≠; duration; clear threshold. |
| What you see | Settings → Alert rules. |
| Alerts | — |
| Configuration | Metric key, operator, threshold, clear threshold, duration, severity, scope, escalation. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-02 · State-change rules

| Field | Content |
|---|---|
| What it does | Alert when a problem state (device down, port down, service stopped …) holds for a time. |
| How it works | 25 state keys (e.g. device.down, iface.down, service.down, dc.replication, hw.component). |
| What you see | Alert rules. |
| Alerts | — |
| Configuration | State key, duration, severity, scope. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-03 · Event-match rules

| Field | Content |
|---|---|
| What it does | Alert on a single matching event (syslog, trap, Windows event, vCenter event, DC security event, reboot); needs acknowledgement. |
| How it works | Filter: regex pattern, minimum severity, event IDs, source substring, trap OID prefix, field equals. |
| What you see | Alert rules. |
| Alerts | — |
| Configuration | Event key + filter. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-04 · Count-in-window rules

| Field | Content |
|---|---|
| What it does | Alert when at least N matching events arrive within a window, per device or per field (e.g. per account). |
| How it works | Count, window, group-by field (targetUser, source, sourceIp …). |
| What you see | Alert rules. |
| Alerts | — |
| Configuration | Count, window, group-by. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-05 · Absence rules

| Field | Content |
|---|---|
| What it does | Alert when no data or events arrive for longer than a period (e.g. no syslog from a firewall for 30 min). |
| How it works | Time-driven evaluation of last data time. |
| What you see | Alert rules. |
| Alerts | No built-in absence rule. |
| Configuration | Key, silence period. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-06 · Severities

| Field | Content |
|---|---|
| What it does | Four severities: Info, Warning, Major, Critical. |
| How it works | Syslog 0–2 → Critical, 3 → Major, 4 → Warning; Windows level 1 → Critical, 2 → Major, 3 → Warning. |
| What you see | Badges, banner, NOC. |
| Alerts | — |
| Configuration | Per rule. |
| Requirements | — |
| Permissions | — |
| Status | ✅ Available |

#### ALR-07 · Alert lifecycle

| Field | Content |
|---|---|
| What it does | Alerts are Active → Acknowledged → Resolved → Closed, with comments and a full history. |
| How it works | Acknowledge (bulk, with comment), comment, close (also open alerts); auto-resolve when the condition clears; event alerts stay until closed; closing lets the condition raise again. |
| What you see | Alerts page. |
| Alerts | — |
| Configuration | Closed-alert retention 365 days. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Act: Operator, Admin |
| Status | ✅ Available |

#### ALR-08 · De-duplication

| Field | Content |
|---|---|
| What it does | One open alert per rule and object; repeats raise its occurrence count instead of new alerts. |
| How it works | Dedup key = rule + device + interface/instance. |
| What you see | Alerts (occurrences, first/last seen). |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### ALR-09 · Alert flapping protection

| Field | Content |
|---|---|
| What it does | An alert that keeps raising and clearing is marked flapping and only resolves after it has been stable. |
| How it works | ≥ 5 changes in 30 min = flapping; resolve held until clear for 15 min. |
| What you see | Alerts (flapping flag). |
| Alerts | — |
| Configuration | Settings → General → Alerting: 5 (3–50) in 30 min, stable 15 min. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-10 · Rule scope

| Field | Content |
|---|---|
| What it does | Limit a rule to devices, groups (with sub-groups), device types, tags, or important interfaces. |
| How it works | RuleScope JSON; empty = all devices. |
| What you see | Rule editor. |
| Alerts | — |
| Configuration | Per rule. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-11 · Dependency suppression

| Field | Content |
|---|---|
| What it does | Alerts of devices whose upstream device is unreachable are suppressed. |
| How it works | Walks the parent chain; suppressed conditions are counted and listed. |
| What you see | System status (suppressed). |
| Alerts | — |
| Configuration | Dependency suppression (on). |
| Requirements | Parent devices (INV-08). |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-12 · Maintenance windows

| Field | Content |
|---|---|
| What it does | Scheduled periods with no new alerts for the devices in scope; polling continues. |
| How it works | One-off, daily, weekly (days) or cron (5 fields), local PKT time, duration; scope like rules. |
| What you see | Settings → Maintenance windows. |
| Alerts | Suppression counted as “suppressed by maintenance”. |
| Configuration | Name, scope, recurrence, start/end, time, days, cron, duration, reason. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-13 · Escalation

| Field | Content |
|---|---|
| What it does | Unacknowledged alerts are escalated after a time: flagged, notified again and the alarm sound repeats. |
| How it works | Critical after 15 min, Major never (default); per-rule override; sound repeats every 5 min. |
| What you see | Alerts (escalated); browser sound. |
| Alerts | — |
| Configuration | Escalate Critical 15 min, Major 0 (off), repeat sound 5 min (0–120); per rule “escalate after”. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-14 · Built-in rules

| Field | Content |
|---|---|
| What it does | 61 rules are created on first start and added on upgrade if missing; they can be edited and disabled but not deleted. |
| How it works | Table A. |
| What you see | Settings → Alert rules. |
| Alerts | Table A. |
| Configuration | Per rule. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### ALR-15 · On-screen pop-up and sound

| Field | Content |
|---|---|
| What it does | New alerts pop up in the browser and an alarm tone plays; Critical uses a siren pattern. |
| How it works | SignalR alert events; Web Audio API tones (no audio files); a critical/major banner on top. |
| What you see | Every page; NOC. |
| Alerts | — |
| Configuration | Sound must be enabled once per browser tab (browser rule). |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### ALR-16 · E-mail (SMTP)

| Field | Content |
|---|---|
| What it does | Sends alert mails through an internal relay. |
| How it works | MailKit; security None, StartTLS (default) or SSL-on-connect; optional auth; ≥ 5 mails in one batch are sent as one digest; retries after 10 s and 60 s on transient errors; addresses validated. |
| What you see | Settings → Notifications (test, status). |
| Alerts | Notification failures → self-check. |
| Configuration | Enabled (off), host, port 25, security StartTLS, user, password (DPAPI), from, to, minimum severity Major, notify on resolve (on), on escalation (on). |
| Requirements | Internal SMTP relay reachable. |
| Permissions | Admin |
| Status | 🧪 Pending pilot — Tested against a test relay only. |

#### ALR-17 · Syslog forwarding

| Field | Content |
|---|---|
| What it does | Forwards alerts as syslog to a SIEM. |
| How it works | RFC 5424 over UDP or TCP (octet-counting RFC 6587); retries on transient errors. |
| What you see | Notifications. |
| Alerts | — |
| Configuration | Enabled (off), host, port 514, UDP, minimum severity Warning. |
| Requirements | SIEM reachable. |
| Permissions | Admin |
| Status | ✅ Available |

#### ALR-18 · Windows Event Log

| Field | Content |
|---|---|
| What it does | Writes alerts to the Application log of the NetWatch server. |
| How it works | Source “NetWatch”; event IDs 1000+severity (raised), 2000 (resolved), 3000+severity (escalated). |
| What you see | Notifications; Event Viewer. |
| Alerts | — |
| Configuration | Enabled (off), minimum severity Warning. |
| Requirements | Source registered by the installer. |
| Permissions | Admin |
| Status | 🧪 Pending pilot — Source registration needs the installer (admin); not yet verified on a real install. |

#### ALR-19 · Script action

| Field | Content |
|---|---|
| What it does | Runs a script for each alert (e.g. to send to another system). |
| How it works | Only files in `C:\ProgramData\NetWatch\scripts` named `*.ps1\|*.cmd\|*.bat\|*.exe` (no folders); PowerShell or cmd; alert JSON on stdin; timeout 30 s. |
| What you see | Notifications (test). |
| Alerts | — |
| Configuration | Enabled (off), file, minimum severity Major, timeout 30 s (5–300). |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### ALR-20 · Notification test and status

| Field | Content |
|---|---|
| What it does | Send a test through each channel and see sent/failed counts and the last error. |
| How it works | `/api/notifications/test/{channel}`, `/api/notifications/status`. |
| What you see | Settings → Notifications. |
| Alerts | “Notifications” self-check. |
| Configuration | — |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

## 9. Dashboards and UI

#### UI-01 · Overview

| Field | Content |
|---|---|
| What it does | Totals by status and type, open alerts, group tiles, top interfaces/CPU/memory/disks, received messages, devices in maintenance, problem devices and recent events. |
| How it works | `/api/overview` + live summary. |
| What you see | / (Overview). |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### UI-02 · Devices

| Field | Content |
|---|---|
| What it does | Searchable, filterable list of all devices with bulk actions (maintenance), add, import and discovery. |
| How it works | Virtualised table. |
| What you see | /devices. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### UI-03 · Device detail

| Field | Content |
|---|---|
| What it does | Everything about one device: status, inventory, graphs, interfaces, sensors, events, server/GPU/hardware/VM panels. |
| How it works | Per-device API endpoints. |
| What you see | /devices/{id}. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### UI-04 · Switches

| Field | Content |
|---|---|
| What it does | Switch list with live port grid. |
| How it works | — |
| What you see | /switches. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### UI-05 · Servers

| Field | Content |
|---|---|
| What it does | Server list with CPU, memory, worst disk, services, method, OS, last boot, pending reboot and last patch. |
| How it works | `/api/servers`. |
| What you see | /servers. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### UI-06 · VMware

| Field | Content |
|---|---|
| What it does | Tabs: Hosts & VMs, Virtual machines, Datastores, Snapshots, vCenter alarms, vCenter events. |
| How it works | `/api/vmware/*`. |
| What you see | /vmware. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### UI-07 · Domain controllers

| Field | Content |
|---|---|
| What it does | Tabs: Overview, Security events, Account lockouts, Group changes, Replication, Recommended audit policy. |
| How it works | `/api/dc/*`. |
| What you see | /dc. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### UI-08 · Topology

| Field | Content |
|---|---|
| What it does | Dependency tree, LLDP/CDP links and parent suggestions. |
| How it works | `/api/topology*`. |
| What you see | /topology. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Change: Admin |
| Status | ✅ Available |

#### UI-09 · Events

| Field | Content |
|---|---|
| What it does | Tabs: Device events, Syslog, SNMP traps, Windows event logs, each with search and time filters. |
| How it works | `/api/events`, `/api/syslog`, `/api/traps`, `/api/windows-events` (page size up to 2,000). |
| What you see | /events. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### UI-10 · Alerts

| Field | Content |
|---|---|
| What it does | Open and past alerts with filters, history, bulk acknowledge/close and comments. |
| How it works | `/api/alerts*`. |
| What you see | /alerts. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Act: Operator, Admin |
| Status | ✅ Available |

#### UI-11 · Reports

| Field | Content |
|---|---|
| What it does | Tabs: Create report, Scheduled reports, Stored reports. |
| How it works | `/api/reports/*`. |
| What you see | /reports. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Act: Operator, Admin |
| Status | ✅ Available |

#### UI-12 · NOC mode

| Field | Content |
|---|---|
| What it does | Full-screen wall display with large counts, group tiles, alert list (refresh 15 s), a rotating panel (problem devices, sites, busy links, DCs), a DC security ticker and sound; scales from 1080p to 4K. |
| How it works | /noc; kiosk sign-in (SEC-12). |
| What you see | /noc. |
| Alerts | Sound for new alerts. |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### UI-13 · Settings

| Field | Content |
|---|---|
| What it does | Tabs: Alert rules, Maintenance windows, Notifications, Credentials, Groups & sites, Users, Directory (AD), General, Audit trail, System status, Pilot tools. |
| How it works | Admin-only route. |
| What you see | /settings. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### UI-14 · Account

| Field | Content |
|---|---|
| What it does | Own profile and password change. |
| How it works | — |
| What you see | /account. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### UI-15 · Dark and light theme

| Field | Content |
|---|---|
| What it does | Switch between dark and light; first choice follows the operating system. |
| How it works | Stored per browser (localStorage). |
| What you see | Top bar toggle. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### UI-16 · Supported browsers and resolutions

| Field | Content |
|---|---|
| What it does | Current Edge, Chrome or Firefox; layout adapts below 900 px; NOC scaling for 1080p–4K. |
| How it works | Responsive CSS, reduced-motion support. |
| What you see | — |
| Alerts | — |
| Configuration | — |
| Requirements | Nothing installed on operator PCs. |
| Permissions | — |
| Status | 🟡 Limited — Browser/4K testing is a pilot item; not checked in a browser during QA or this run. |

## 10. Reports

#### RPT-01 · Availability report

| Field | Content |
|---|---|
| What it does | Uptime per device (ping/SNMP), outages, approximate downtime and latency, worst devices first. |
| How it works | Period report. |
| What you see | Reports. |
| Alerts | — |
| Configuration | Period, group, format. |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### RPT-02 · Alert history report

| Field | Content |
|---|---|
| What it does | Alerts raised in the period by severity, rule and device, with time to acknowledge and to resolve. |
| How it works | Period report. |
| What you see | Reports. |
| Alerts | — |
| Configuration | Period, group, format. |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### RPT-03 · Interface utilization report

| Field | Content |
|---|---|
| What it does | Average and peak in/out utilization of important interfaces, busiest first. |
| How it works | Period report. |
| What you see | Reports. |
| Alerts | — |
| Configuration | Period, group, format. |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### RPT-04 · Server capacity report

| Field | Content |
|---|---|
| What it does | CPU and memory trends of servers, volume and datastore fill levels with growth, and disks predicted to fill within 30 days. |
| How it works | Linear trend (CapacityMath). |
| What you see | Reports. |
| Alerts | — |
| Configuration | Period, group, format. |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available — Prediction is a simple linear trend. |

#### RPT-05 · Domain controller security report

| Field | Content |
|---|---|
| What it does | Failed logons, lockouts, privileged group changes, new accounts and audit events. |
| How it works | Period report. |
| What you see | Reports. |
| Alerts | — |
| Configuration | Period, group, format. |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### RPT-06 · Device inventory report

| Field | Content |
|---|---|
| What it does | All devices with type, address, group, vendor, model, serial number, firmware and status (current state). |
| How it works | Snapshot report. |
| What you see | Reports. |
| Alerts | — |
| Configuration | Group, format. |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### RPT-07 · VMware report

| Field | Content |
|---|---|
| What it does | ESXi hosts (CPU, memory, state), datastore capacity, snapshots with age and size, and powered-off VMs (current state). |
| How it works | Snapshot report. |
| What you see | Reports. |
| Alerts | — |
| Configuration | Group, format. |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### RPT-08 · PDF and CSV formats

| Field | Content |
|---|---|
| What it does | Every report as PDF (built-in writer, no external tool) or CSV (UTF-8 with BOM, formula-injection safe). |
| How it works | PdfReportRenderer / CsvReportRenderer. |
| What you see | Reports → format. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | 🟡 Limited — PDFs in English only; max 366 days per report. |

#### RPT-09 · Preview

| Field | Content |
|---|---|
| What it does | See the report on screen before downloading. |
| How it works | `/api/reports/preview`. |
| What you see | Reports → Create report. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Viewer, Operator, Admin |
| Status | ✅ Available |

#### RPT-10 · Scheduled reports

| Field | Content |
|---|---|
| What it does | Run a report daily, weekly or monthly and store it; each run covers the previous day, 7 days or calendar month. |
| How it works | Checked every minute; local PKT time. |
| What you see | Reports → Scheduled reports (run now, last status). |
| Alerts | “Scheduled reports” self-check. |
| Configuration | Name, type, format, frequency, time (07:00), weekday, day of month (1–28), group, recipients. |
| Requirements | — |
| Permissions | View: Viewer, Operator, Admin · Act: Operator, Admin |
| Status | ✅ Available |

#### RPT-11 · E-mail delivery of reports

| Field | Content |
|---|---|
| What it does | Scheduled reports are mailed as attachments. |
| How it works | Uses the SMTP channel settings. |
| What you see | Schedule → recipients. |
| Alerts | — |
| Configuration | Recipients per schedule. |
| Requirements | SMTP configured (ALR-16). |
| Permissions | View: Viewer, Operator, Admin · Act: Operator, Admin |
| Status | 🧪 Pending pilot — Depends on a real SMTP relay (pilot). |

#### RPT-12 · Stored reports

| Field | Content |
|---|---|
| What it does | Generated files are kept per month for download and deleted after 400 days. |
| How it works | `C:\ProgramData\NetWatch\reports\yyyy-MM\`. |
| What you see | Reports → Stored reports. |
| Alerts | — |
| Configuration | Fixed 400 days. |
| Requirements | — |
| Permissions | View: Viewer+ · Delete: Operator, Admin |
| Status | ✅ Available |

## 11. Backup and restore

#### BAK-01 · Automatic daily backup

| Field | Content |
|---|---|
| What it does | Every night a consistent backup of the databases and configuration is written while the service keeps running. |
| How it works | SQLite `VACUUM INTO` per database + appsettings.json + certificates, zipped with a manifest and SHA-256 checksums; daily at 02:00 PKT; keep 14. |
| What you see | Settings → System → Backups. |
| Alerts | “Backups” self-check (newest older than 36 h or last failed). |
| Configuration | Enabled (on), time 02:00, include metrics (on), keep 14 (1–365), folder (default `backups\`, or absolute local path / UNC share). |
| Requirements | Write access to the target folder for LocalSystem (UNC: computer account). |
| Permissions | Admin |
| Status | ✅ Available |

#### BAK-02 · Manual backup and download

| Field | Content |
|---|---|
| What it does | Make a backup now, download or delete backup files. |
| How it works | `POST /api/backups`, download, delete. |
| What you see | Backups panel. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### BAK-03 · Portable backup (credentials)

| Field | Content |
|---|---|
| What it does | A backup whose credentials can be restored on another server. |
| How it works | Secrets re-encrypted with AES-256-GCM, key from PBKDF2-SHA256 600,000 iterations of an admin-chosen password (never stored). |
| What you see | Backups → Portable backup. |
| Alerts | — |
| Configuration | Password per backup. |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### BAK-04 · Backup validation

| Field | Content |
|---|---|
| What it does | A backup is checked before restore: manifest, checksums, allowed paths, and the portable password. |
| How it works | Zip allow-list and path checks. |
| What you see | Restore dialog. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### BAK-05 · Restore from the web console

| Field | Content |
|---|---|
| What it does | Choose a backup and restore it; the service restarts and restores before opening any database. |
| How it works | Writes a pending-restore marker and stops; the next start moves current files to `before-restore-…` and restores; service recovery restarts it. |
| What you see | Backups → Restore (type-to-confirm). |
| Alerts | — |
| Configuration | — |
| Requirements | Installed service (recovery on non-zero exit). |
| Permissions | Admin |
| Status | ✅ Available |

#### BAK-06 · Restore from the command line

| Field | Content |
|---|---|
| What it does | Restore a backup file with the service stopped. |
| How it works | `NetWatch.Service.exe --restore <file.zip> [--restore-password]`. |
| What you see | — |
| Alerts | — |
| Configuration | — |
| Requirements | Administrator on the server. |
| Permissions | Server administrators |
| Status | ✅ Available |

#### BAK-07 · Move to a new server

| Field | Content |
|---|---|
| What it does | Install NetWatch on the new server and restore a portable backup there. |
| How it works | BAK-03 + BAK-05/06; DPAPI secrets re-protected for the new machine. |
| What you see | — |
| Alerts | — |
| Configuration | — |
| Requirements | Same or newer NetWatch version. |
| Permissions | Admin |
| Status | 🧪 Pending pilot — Restore into a new folder tested; restore on a second machine not yet run. |

## 12. Pilot and support tools

#### PIL-01 · Connection tests

| Field | Content |
|---|---|
| What it does | Step-by-step test of one protocol against one device, with a clear error and history. |
| How it works | 10 kinds: Ping, SNMP, WinRM (WMI query), Event log, SSH, vCenter, Redfish, LDAP (bind as a given account, never stored), SMTP, Receivers (syslog/traps received from a device in the last N minutes, default 15). |
| What you see | Settings → Pilot tools. |
| Alerts | — |
| Configuration | Device or address, credential. |
| Requirements | As for the protocol. |
| Permissions | Admin |
| Status | ✅ Available — Ping verified here; the other kinds return clear errors but need real devices to prove success (pilot). |

#### PIL-02 · Diagnostic bundle

| Field | Content |
|---|---|
| What it does | One zip for support: logs (24 h), connection tests, poll statistics, redacted settings and appsettings, credential names (no secrets), inventory, and SNMP walks of up to 20 chosen devices. |
| How it works | `/api/diagnostics/bundle`; any property named like a secret is removed at any depth. |
| What you see | Pilot tools → Download bundle. |
| Alerts | — |
| Configuration | Devices to walk. |
| Requirements | — |
| Permissions | Admin |
| Status | ✅ Available |

#### PIL-03 · Simulator mode

| Field | Content |
|---|---|
| What it does | Runs NetWatch with simulated devices so every screen and alert can be tried without hardware. |
| How it works | `--simulate` or `Simulator:Enabled`: 120 switches (2 core, 8 distribution, 110 access), 20 servers (15 Windows, 5 Linux), 40 VMs, a vCenter with 4 ESXi hosts, 2 DCs (PDC + 1), with traffic, faults, reboots, filling disks, stopped services, event-log errors, syslog, traps, DC security events, hardware faults. All tagged `simulated`. Rate of incidents via `NETWATCH_SIM_INCIDENT_RATE`. |
| What you see | All pages. |
| Alerts | All rule types fire. |
| Configuration | appsettings.json or command line. |
| Requirements | — |
| Permissions | — |
| Status | ✅ Available |

#### PIL-04 · Self-test

| Field | Content |
|---|---|
| What it does | Starts the full service (databases, seeding, all background services, HTTPS), runs briefly and stops cleanly — to check an install or upgrade. |
| How it works | `NetWatch.Service.exe --self-test --data-dir <folder>`. |
| What you see | Console output / exit code. |
| Alerts | — |
| Configuration | — |
| Requirements | Service stopped. |
| Permissions | Server administrators |
| Status | ✅ Available |

#### PIL-05 · Other command-line options

| Field | Content |
|---|---|
| What it does | Console mode, alternate data folder, admin reset, version, certificate setup. |
| How it works | `--console`, `--data-dir <dir>`, `--reset-admin`, `--version`, `--set-certificate <file.pfx>`, `--restore <zip>`, `--simulate`. |
| What you see | — |
| Alerts | — |
| Configuration | — |
| Requirements | Administrator on the server. |
| Permissions | Server administrators |
| Status | ✅ Available |

## 13. Installation and upgrade

#### INS-01 · Windows installer

| Field | Content |
|---|---|
| What it does | One installer that puts the program in Program Files, creates and starts the service and shows the console address and where the first password is. |
| How it works | Inno Setup 6, x64, admin rights; `C:\Program Files\NetWatch`, data `C:\ProgramData\NetWatch`; Start-menu shortcuts (dashboard, documentation, data folder, uninstall); docs copied to `{app}\docs`. |
| What you see | Setup wizard. |
| Alerts | — |
| Configuration | Silent: `/VERYSILENT /SUPPRESSMSGBOXES /LOG=…`. |
| Requirements | Local administrator. |
| Permissions | — |
| Status | 🧪 Pending pilot — Built by the offline build; not yet executed on Windows Server 2022 (release gate). |

#### INS-02 · Firewall rules

| Field | Content |
|---|---|
| What it does | Opens the inbound ports, bound to the NetWatch executable only. |
| How it works | “NetWatch HTTPS” TCP 8443, “NetWatch SNMP traps” UDP 162, “NetWatch syslog UDP” UDP 514, “NetWatch syslog TCP” TCP 514. |
| What you see | Windows Defender Firewall. |
| Alerts | — |
| Configuration | Adjust manually if receiver ports are changed. |
| Requirements | — |
| Permissions | — |
| Status | 🧪 Pending pilot — As INS-01. |

#### INS-03 · Data folder protection

| Field | Content |
|---|---|
| What it does | Only SYSTEM and local Administrators can read the data folder. |
| How it works | icacls: inheritance removed, full control for S-1-5-18 and S-1-5-32-544. |
| What you see | — |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | — |
| Status | 🧪 Pending pilot — As INS-01. |

#### INS-04 · Upgrade in place

| Field | Content |
|---|---|
| What it does | Running the new installer stops the service, replaces the program, keeps all data and upgrades the databases at start. |
| How it works | Same AppId; `sc config`; EF migrations; missing built-in rules added. Upgrade from 1.2.0 data covered by a test. |
| What you see | — |
| Alerts | — |
| Configuration | — |
| Requirements | Copy the data folder first (ROLLBACK.md). |
| Permissions | — |
| Status | 🧪 Pending pilot — Database upgrade 1.2.0 → 2.0.0 tested; installer upgrade not yet run on Server 2022. |

#### INS-05 · Uninstall

| Field | Content |
|---|---|
| What it does | Removes the service, firewall rules and program; asks whether to delete the data (default No). Silent uninstall always keeps data. |
| How it works | Inno Setup uninstall step. |
| What you see | Settings → Apps. |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | — |
| Status | 🧪 Pending pilot — As INS-01. |

#### INS-06 · Offline build

| Field | Content |
|---|---|
| What it does | The whole product can be built inside the air gap from a prepared package folder. |
| How it works | `build.ps1 -PrepareOffline` (online PC) fills `offline\`; `build.ps1 -Offline` builds UI, tests, single-file exe and installer with no network. |
| What you see | — |
| Alerts | — |
| Configuration | — |
| Requirements | .NET 10 SDK, Node.js 20+, Inno Setup 6 on the build PC. |
| Permissions | — |
| Status | ✅ Available |

#### INS-07 · Locked packages

| Field | Content |
|---|---|
| What it does | Builds fail if any package was changed or substituted. |
| How it works | `packages.lock.json` (versions + content hashes) per project, locked restore; `npm ci` with integrity hashes. |
| What you see | — |
| Alerts | — |
| Configuration | — |
| Requirements | — |
| Permissions | — |
| Status | ✅ Available |

#### INS-08 · Code signing (optional)

| Field | Content |
|---|---|
| What it does | Sign the exe and installer with your organisation’s certificate. |
| How it works | `build.ps1 -SignCertificate <pfx> [-SignTimestampUrl …]`, password from `NETWATCH_SIGN_PASSWORD`. |
| What you see | — |
| Alerts | — |
| Configuration | — |
| Requirements | Code-signing certificate. |
| Permissions | — |
| Status | 🟡 Limited — Delivered builds are unsigned; allow by hash/path in AppLocker/WDAC. |


---

# Reference tables

## A. Default alert rules

61 built-in rules, all enabled on a new installation. Durations are how long the condition must hold before
the alert is raised; “clears at” is the hysteresis level. “Important ports” = interfaces flagged important.

| # | Name | Type | Condition | Duration | Severity | Scope | Enabled |
|---|---|---|---|---|---|---|---|
| A-01 | Device down | StateChange | state `device.down` | immediate | Critical | all devices | Yes |
| A-02 | High ping latency | Threshold | `ping.rtt` > 100 (clears at 80) | 5 min | Warning | all devices | Yes |
| A-03 | Ping packet loss | Threshold | `ping.loss` > 20 (clears at 10) | 3 min | Warning | all devices | Yes |
| A-04 | High CPU | Threshold | `cpu.pct` > 90 (clears at 85) | 5 min | Major | all devices | Yes |
| A-05 | High memory | Threshold | `mem.pct` > 90 (clears at 85) | 5 min | Major | all devices | Yes |
| A-06 | Important port down | StateChange | state `iface.down` | immediate | Major | important ports | Yes |
| A-07 | High interface utilization | Threshold | `iface.util` > 90 (clears at 80) | 5 min | Warning | important ports | Yes |
| A-08 | Port flapping | StateChange | state `iface.flapping` | immediate | Warning | all devices | Yes |
| A-09 | Important port half duplex | StateChange | state `iface.duplex` | immediate | Major | important ports | Yes |
| A-10 | Important port below expected speed | StateChange | state `iface.speed` | immediate | Warning | important ports | Yes |
| A-11 | Hardware sensor problem | StateChange | state `sensor.problem` | immediate | Major | all devices | Yes |
| A-12 | SNMP not responding | StateChange | state `snmp.unreachable` | 3 min | Warning | all devices | Yes |
| A-13 | Device rebooted | EventMatch | event `device.reboot` | each event | Warning | all devices | Yes |
| A-14 | Disk space low | Threshold | `disk.used.pct` > 85 (clears at 80) | 2 min | Warning | all devices | Yes |
| A-15 | Disk space critical | Threshold | `disk.used.pct` > 95 (clears at 92) | 2 min | Critical | all devices | Yes |
| A-16 | Service not running | StateChange | state `service.down` | 1 min | Major | all devices | Yes |
| A-17 | Server monitoring failing | StateChange | state `server.unreachable` | 5 min | Warning | all devices | Yes |
| A-18 | High load (Linux) | Threshold | `load.per.cpu` > 2 (clears at 1.5) | 10 min | Warning | all devices | Yes |
| A-19 | Windows critical event | EventMatch | event `windows.event` where minSeverity=Critical | each event | Major | all devices | Yes |
| A-20 | Syslog emergency/alert/critical | EventMatch | event `syslog.message` where minSeverity=Critical | each event | Major | all devices | Yes |
| A-21 | SNMP authentication failure | EventMatch | event `snmp.trap` where oid=1.3.6.1.6.3.1.1.5.5 | each event | Warning | all devices | Yes |
| A-22 | Repeated login failures (syslog) | CountInWindow | ≥ 10 × `syslog.message` where pattern=`authentication fail\|failed password\|login fail\|invalid user\|bad password\|failed login`, counted per device | 5 min window | Warning | all devices | Yes |
| A-23 | vCenter/ESXi API not reachable | StateChange | state `vmware.vcenter.unreachable` | 3 min | Major | all devices | Yes |
| A-24 | ESXi host not connected | StateChange | state `vmware.host.disconnected` | immediate | Critical | all devices | Yes |
| A-25 | ESXi host hardware alarm | StateChange | state `vmware.host.hardware` | immediate | Major | all devices | Yes |
| A-26 | ESXi host high CPU | Threshold | `vmware.host.cpu.pct` > 90 (clears at 85) | 5 min | Major | all devices | Yes |
| A-27 | ESXi host high memory | Threshold | `vmware.host.mem.pct` > 90 (clears at 85) | 5 min | Major | all devices | Yes |
| A-28 | Datastore space low | Threshold | `vmware.datastore.used.pct` > 85 (clears at 80) | immediate | Warning | all devices | Yes |
| A-29 | Datastore space critical | Threshold | `vmware.datastore.used.pct` > 95 (clears at 92) | immediate | Critical | all devices | Yes |
| A-30 | Datastore not accessible | StateChange | state `vmware.datastore.inaccessible` | immediate | Critical | all devices | Yes |
| A-31 | Old snapshot | Threshold | `vmware.snapshot.age.days` > 7 | immediate | Warning | all devices | Yes |
| A-32 | Large snapshot | Threshold | `vmware.snapshot.size.gb` > 50 (clears at 40) | immediate | Warning | all devices | Yes |
| A-33 | VM CPU ready high | Threshold | `vmware.vm.cpuready.pct` > 10 (clears at 5) | 10 min | Warning | all devices | Yes |
| A-34 | VMware Tools not running | StateChange | state `vmware.vm.tools` | 15 min | Warning | all devices | Yes |
| A-35 | vCenter alarm (red) | StateChange | state `vmware.alarm.red` | immediate | Major | all devices | Yes |
| A-36 | vCenter alarm (yellow) | StateChange | state `vmware.alarm.yellow` | immediate | Warning | all devices | Yes |
| A-37 | vCenter major event | EventMatch | event `vmware.event` where minSeverity=Major | each event | Major | all devices | Yes |
| A-38 | Failed logons for one account | CountInWindow | ≥ 5 × `dc.security` where fieldEquals=failed:true, counted per targetUser | 5 min window | Warning | all devices | Yes |
| A-39 | Failed logons from one source | CountInWindow | ≥ 20 × `dc.security` where fieldEquals=failed:true, counted per source | 5 min window | Major | all devices | Yes |
| A-40 | Account locked out | EventMatch | event `dc.security` where eventIds=4740 | each event | Major | all devices | Yes |
| A-41 | Privileged group membership added | EventMatch | event `dc.security` where eventIds=4728,4732,4756; fieldEquals=privileged:true | each event | Critical | all devices | Yes |
| A-42 | Security log cleared | EventMatch | event `dc.security` where eventIds=1102 | each event | Critical | all devices | Yes |
| A-43 | Audit policy changed | EventMatch | event `dc.security` where eventIds=4719 | each event | Critical | all devices | Yes |
| A-44 | Admin logon outside working hours | EventMatch | event `dc.security` where eventIds=4672; fieldEquals=offHours:true | each event | Warning | all devices | Yes |
| A-45 | New user account created | EventMatch | event `dc.security` where eventIds=4720 | each event | Warning | all devices | Yes |
| A-46 | New computer account created | EventMatch | event `dc.security` where eventIds=4741 | each event | Warning | all devices | Yes |
| A-47 | User or computer account deleted | EventMatch | event `dc.security` where eventIds=4726,4743 | each event | Warning | all devices | Yes |
| A-48 | Kerberos pre-authentication failures spike | CountInWindow | ≥ 50 × `dc.security` where eventIds=4771, counted per device | 5 min window | Major | all devices | Yes |
| A-49 | AD replication failing | StateChange | state `dc.replication` | 1 h | Major | all devices | Yes |
| A-50 | AD replication stale | StateChange | state `dc.replication.stale` | immediate | Major | all devices | Yes |
| A-51 | DC service port unreachable | StateChange | state `dc.port` | 2 min | Major | all devices | Yes |
| A-52 | Directory/DFS replication error | EventMatch | event `windows.event` where eventIds=1311,1864,2042,2087,2088,2213,4012 | each event | Major | all devices | Yes |
| A-53 | DC clock skew | Threshold | `dc.time.skew.sec` > 30 (clears at 20) | 5 min | Warning | all devices | Yes |
| A-54 | DC clock skew critical | Threshold | `dc.time.skew.sec` > 120 (clears at 100) | immediate | Critical | all devices | Yes |
| A-55 | GPU utilization high | Threshold | `gpu.util.pct` > 95 (clears at 85) | 10 min | Warning | all devices | Yes |
| A-56 | GPU temperature high | Threshold | `gpu.temp.c` > 85 (clears at 80) | 2 min | Major | all devices | Yes |
| A-57 | GPU memory high | Threshold | `gpu.mem.pct` > 90 (clears at 85) | 5 min | Warning | all devices | Yes |
| A-58 | GPU missing or driver error | StateChange | state `gpu.problem` | immediate | Major | all devices | Yes |
| A-59 | Server hardware component not OK | StateChange | state `hw.component` | immediate | Major | all devices | Yes |
| A-60 | Management controller unreachable | StateChange | state `hw.unreachable` | 15 min | Warning | all devices | Yes |
| A-61 | NetWatch self-check failing | StateChange | state `netwatch.selfcheck` | 2 min | Major | all devices | Yes |

Escalation for all of them: unacknowledged **Critical** alerts escalate after **15 min**; Major never (default).

## B. Network ports

| Port | Protocol | Direction | Between | Purpose | Configurable |
|---|---|---|---|---|---|
| 8443 | TCP (HTTPS) | Inbound (listens) | Operator browsers → NetWatch | Web console, REST API, SignalR | `Https:Port` |
| 162 | UDP | Inbound (listens) | Devices → NetWatch | SNMP traps and informs (inform responses go back to the sender) | Settings → Receivers |
| 514 | UDP | Inbound (listens) | Devices → NetWatch | Syslog | Settings → Receivers |
| 514 | TCP | Inbound (listens) | Devices → NetWatch | Syslog over TCP | Settings → Receivers |
| — | ICMP echo | Outbound | NetWatch → all devices | Availability, latency, loss; discovery | — |
| 161 | UDP | Outbound | NetWatch → switches, routers, firewalls, servers, iLO/iDRAC (fallback) | SNMP v2c/v3 polling, discovery, hardware fallback | — |
| 5985 / 5986 | TCP | Outbound | NetWatch → Windows servers and DCs | WinRM (WMI over WS-Management), nvidia-smi | Settings → Servers |
| 135 + dynamic RPC | TCP | Outbound | NetWatch → Windows servers and DCs | Remote event logs (System, Application, Security, Directory Service, DFS Replication); WMI over DCOM fallback | — |
| 22 | TCP | Outbound | NetWatch → Linux servers | SSH collection | Settings → Servers |
| 443 | TCP (HTTPS) | Outbound | NetWatch → vCenter / ESXi | vSphere API (`/sdk`) | — |
| 443 | TCP (HTTPS) | Outbound | NetWatch → iLO / iDRAC | Redfish hardware health | — |
| 389 | TCP | Outbound | NetWatch → domain controllers | Replication status, FSMO/site (signed + sealed LDAP); port check | — |
| 88 | TCP | Outbound | NetWatch → domain controllers | Kerberos port reachability check | — |
| 53 | TCP | Outbound | NetWatch → domain controllers | DNS port reachability check | — |
| 636 (or 389) | TCP | Outbound | NetWatch → domain controllers | Optional: AD sign-in (LDAPS / StartTLS / Negotiate) | Settings → Directory |
| 25 (587/465) | TCP | Outbound | NetWatch → SMTP relay | Optional: alert and report e-mail | Settings → Notifications |
| 514 | UDP or TCP | Outbound | NetWatch → SIEM | Optional: syslog forwarding of alerts | Settings → Notifications |
| 5985 | TCP | Inbound to the *Windows Event Collector* service (not NetWatch) | DCs → NetWatch server | Optional: Windows Event Forwarding subscription | WEF configuration |

## C. Device-side requirements

| Device type | Enable on the device | Account and rights |
|---|---|---|
| Switches, routers, firewalls | SNMP agent (v3 authPriv recommended, SHA-256+ / AES); ACL allowing only the NetWatch server; for topology LLDP or CDP; optionally send traps (UDP 162) and syslog (UDP/TCP 514) to NetWatch | Read-only community or SNMPv3 user with a read view. NetWatch never writes |
| Windows servers | WinRM (`winrm quickconfig`; HTTP 5985 or HTTPS 5986); firewall rules *Windows Remote Management* and *Remote Event Log Management*; for DCOM fallback RPC 135 + dynamic | Domain or local account in **Remote Management Users** (or **Distributed COM Users**), **Event Log Readers**; WMI *Remote Enable* on `root\cimv2` and `root\default` (pending reboot); remote shell rights for nvidia-smi |
| Linux servers | SSH server; `nvidia-smi` for NVIDIA values; `lspci` for GPU list | Normal user (reads /proc, df, systemctl show; no root). Host key is pinned on first connection |
| Servers via SNMP only | SNMP agent with HOST-RESOURCES-MIB | Read-only community / v3 user |
| HPE iLO / Dell iDRAC | Redfish over HTTPS (443) enabled; optional SNMP for the fallback | Local read-only account (iLO: *Login* privilege only; iDRAC: *Read Only* role) |
| vCenter / ESXi | HTTPS 443 reachable | Account with the **Read-only** role assigned at the vCenter root, *propagate to children* |
| Domain controllers | Advanced audit policy (below) via GPO on the Domain Controllers OU, with *Force audit policy subcategory settings*; Security log ≥ 1 GB; WinRM; firewall rules for WinRM and Remote Event Log Management; TCP 389/88/53 reachable | Domain user (not a Domain Admin) in **Event Log Readers** and **Remote Management Users** (or Distributed COM Users); WMI Remote Enable on `root\cimv2`. For WEF: subscription to the NetWatch server, NETWORK SERVICE in Event Log Readers on the DCs, NetWatch service account in local Event Log Readers |
| SMTP relay (optional) | Accept mail from the NetWatch server | Optional SMTP account |

Audit policy needed on DCs (*Advanced Audit Policy Configuration → Audit Policies*):

| Category → subcategory | Setting | Event IDs |
|---|---|---|
| Account Logon → Credential Validation | Success, Failure | 4776 |
| Account Logon → Kerberos Authentication Service | Success, Failure | 4768, 4771 |
| Account Management → User Account Management | Success, Failure | 4720, 4722, 4723, 4724, 4725, 4726, 4738, 4740, 4767 |
| Account Management → Computer Account Management | Success | 4741, 4743 |
| Account Management → Security Group Management | Success | 4728, 4729, 4732, 4733, 4756, 4757 |
| Logon/Logoff → Logon | Success, Failure | 4624, 4625 |
| Logon/Logoff → Special Logon | Success | 4672 |
| Policy Change → Audit Policy Change | Success | 4719 |
| DS Access → Directory Service Changes | Success (plus a SACL on the objects to watch) | 5136 |
| (always logged) | — | 1102 |

## D. System requirements and tested scale

**Tested scale (measured, simulator at 2× scale, Release build, one process):** 306 devices (240 switches, 20 servers, 40 VMs, 2 domain controllers, 1 vCenter with 4 ESXi hosts), **≈ 12,470 interfaces**, 60 s poll interval, 64 parallel polls, 5 % slow devices and 2 % SNMP time-outs, DC Security events offered at **≈ 1,000/min** (≈ 780/min stored after the default 4624 filter) and, in the 4-hour soaks, **4,000/min**. Source: `docs/security-review.md` §2 and `docs/qa/RELEASE_REPORT.md` §5.

| Measured value | Result |
|---|---|
| Polls | ≈ 310–324 per minute (every device every 60 s); average poll 280–450 ms; longest 4–7 s (simulated time-outs) |
| Overruns / dropped samples / dropped alert signals | 0 / 0 / 0 in three runs; 1 overrun in the first soak (BUG-006, open) |
| CPU | ≈ 2–2.5 % of one core on average |
| Memory (working set) | 262–342 MB; flat at ≈ 327 MB in hours 3–4 of soak 2 (24 h test outstanding) |
| metrics.db growth | ≈ 1.2 GB/day before roll-up retention takes effect |
| events.db growth | ≈ 0.9 GB/day at 1,000 DC events/min; ≈ 2.8 GB/day at 4,000/min |
| API response | 42 main endpoints: max 41 ms, average 12 ms; 500-row CSV import 122 ms |
| This run (1× simulator, 188 devices, 6,332 interfaces) | 47 GET endpoints all HTTP 200 in ≤ 0.12 s; 304/304 unit and integration tests passed |

**Recommended NetWatch server**

| Item | Requirement |
|---|---|
| OS | Windows Server 2019, 2022 or 2025 (or Windows 10 1809+/11), 64-bit. Target: Windows Server 2022 |
| CPU / RAM | **2 vCPU / 4 GB** up to the tested scale (≈ 300 devices, 12,500 interfaces) — measured use is < 3 % of one core and < 350 MB. The installation guide’s 4 vCPU / 8 GB for ≈ 1,000 devices is an estimate, not measured |
| Disk (data drive) | Minimum 20 GB free to install. Metric history ≈ 4 MB per monitored interface when full (≈ 50 GB for 12,500 interfaces after 2 years). DC events add ≈ 0.9 GB per day at 1,000 events/min — with the default 365-day event retention that is ≈ 330 GB; reduce *event retention* (e.g. 90 days ≈ 80 GB) or the stored logon types if the disk is smaller. Keep backups on another disk |
| Network | See table B |

## E. Data retention defaults

| Data | Default | Range | Where set |
|---|---|---|---|
| Raw metric samples (every poll) | 7 days | 1–60 days | Settings → General → Retention |
| 5-minute averages | 90 days | 7–730 days | same |
| Hourly averages | 730 days | 30–3650 days | same |
| Events (device events, syslog, traps, Windows events, DC security events, vCenter events) | 365 days | 7–3650 days | same |
| Audit trail | 365 days | 30–3650 days | same |
| Closed alerts | 365 days | 7–3650 days | same |
| Expired sessions | removed nightly | — | — |
| Stored report files | 400 days | fixed | — |
| Backup files | newest 14 kept | 1–365 files | Settings → System → Backups |
| Service log files | 30 days | 1–365 | `Logging:RetainedDays` in appsettings.json |
| Connection-test history | in memory (until restart) | — | — |

The purge runs nightly at 02:30 PKT. Metric shards are dropped whole (day / month / year), so data can be kept up to one shard period longer than set.

## F. Third-party components

| Component | Version | Licence | Used for |
|---|---|---|---|
| .NET runtime / ASP.NET Core (bundled in the single-file exe) | 10.0 (SDK 10.0.404) | MIT | Service, web server, SignalR server |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.15 | MIT | Configuration and events databases |
| SQLitePCLRaw (e_sqlite3) | 2.1.13 | Apache-2.0 (SQLite itself: public domain) | SQLite engine |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.15 | MIT | Windows service host |
| Microsoft.Extensions.FileProviders.Embedded | 10.0.15 | MIT | Embedded UI |
| System.Security.Cryptography.ProtectedData | 10.0.15 | MIT | DPAPI |
| System.Management | 10.0.15 | MIT | WMI over DCOM |
| System.Diagnostics.EventLog | 10.0.15 | MIT | Remote event logs, Event Log channel |
| System.DirectoryServices.Protocols | 10.0.15 | MIT | LDAP (AD sign-in, DC replication) |
| Lextm.SharpSnmpLib | 12.5.7 | MIT | SNMP v1/v2c/v3 client and trap decoding |
| SSH.NET | 2026.0.1 | MIT | Linux collection over SSH |
| MailKit / MimeKit | 4.18.2 | MIT | SMTP |
| BouncyCastle.Cryptography (MailKit dependency) | 2.7.0 | MIT | Cryptography for MailKit |
| Serilog.AspNetCore | 10.0.0 | Apache-2.0 | Logging |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | Log files |
| Serilog.Settings.Configuration | 10.0.1 | Apache-2.0 | Log configuration |
| Cronos | 0.9.0 | MIT | Cron schedules (maintenance windows, retention) |
| CsvHelper | 33.1.0 | MS-PL OR Apache-2.0 | CSV import/export |
| React / React DOM | 18.3.1 | MIT | Web UI |
| react-router-dom | 6.28.0 | MIT | UI routing |
| @tanstack/react-query | 5.62.0 | MIT | UI data fetching |
| @tanstack/react-virtual | 3.10.9 | MIT | Large tables |
| @microsoft/signalr | 8.0.7 | MIT | Live updates (browser) |
| ECharts / ZRender | 5.5.1 / 5.6.0 | Apache-2.0 / BSD-3-Clause | Graphs |
| lucide-react | 0.460.0 | ISC | Icons |
| Inno Setup | 6 | Inno Setup licence (free) | Installer (build time) |
| Build/test only: xunit 2.9.3 (Apache-2.0), Microsoft.NET.Test.Sdk 17.12.0 (MIT), TypeScript 5.6.3 (Apache-2.0), Vite 5.4.11 (MIT), Vitest 2.1.8 (MIT) | | | Not shipped |

Versions from `Directory.Packages.props`, `packages.lock.json` and `ClientApp/package-lock.json`; licences from the package metadata in `offline/`. Transitive packages of `@microsoft/signalr` (e.g. `ws`, `eventsource`, `fetch-cookie` — Unlicense, `tough-cookie` — BSD-3-Clause) are Node-only paths and are listed in package-lock.json.

## G. Not available

None of these is stubbed in the code; they are gaps found while compiling this list.

| ID | Area | Feature | What is missing |
|---|---|---|---|
| NA-01 ⛔ | Switch monitoring | SNMP v1 polling | Polling uses v2c or v3 only (v1 traps are received). Devices that only speak SNMP v1 cannot be polled. |
| NA-02 ⛔ | Switch monitoring | Alerts on port discards | Discards are stored and graphed, but there is no metric key, so no rule can alert on them. |
| NA-03 ⛔ | Device inventory | CSV import dry run | The import creates valid rows immediately; there is no preview/validate-only step (discovery does have a review step). |
| NA-04 ⛔ | Platform | High availability | Single server only; no cluster, standby or replication of the databases. |
| NA-05 ⛔ | Platform | Configurable time zone | Schedules and display are fixed to PKT (UTC+5). |
| NA-06 ⛔ | Reports | Report languages other than English; periods over 366 days | Reports are English only and limited to 366 days per run. |
| NA-07 ⛔ | Switch monitoring | SNMP profile editor in the UI | Custom health profiles are edited only in `snmp-profiles.json` on the server. |
| NA-08 ⛔ | Server monitoring | Hardware and GPU settings in the console | `HardwareSettings` (poll interval 5 min, timeout 30 s, trust on first use, read GPUs, run nvidia-smi) is read by the collectors but has no API endpoint or settings tab, so the defaults cannot be changed. |
