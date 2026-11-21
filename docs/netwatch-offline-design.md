# NetWatch (Offline Edition): Phase 0 design for review

Status: **Approved.** Phase 1 delivered (1.0.0); Phase 2 delivered (1.1.0, section 5); Phase 3 delivered (1.2.0, section 6); Phase 4 delivered (1.3.0, section 7); Phase 5 delivered (1.4.0, section 8).
Date: 2026-11-18

---

## 0. Decisions and assumptions to confirm

| # | Topic | Proposal / assumption |
|---|-------|-----------------------|
| D1 | **Runtime** | **Confirmed: .NET 10 LTS** (supported until Nov 2028) instead of .NET 8, which reached end of support on 10 Nov 2026. |
| D0 | **Repo** | **Confirmed:** the offline .NET solution replaces the Next.js/Supabase app at the root of `D:\ping-monitor`. The old app is preserved in git (tag `legacy-cloud-netwatch`) before removal. |
| D2 | **Installer** | **Inno Setup 6**: it builds a single `.exe` directly, it is free for commercial use, and it handles service install/upgrade with a short script. WiX v4 builds an MSI and needs a Burn bundle to produce an `.exe`. |
| D3 | Scheduler | A custom poll scheduler (hosted service + `System.Threading.Channels` + `PeriodicTimer`, with jitter and per-protocol concurrency limits) for the 60 s polling. **Cronos** parses cron expressions for reports, backup and retention. Quartz.NET is not needed. |
| D4 | WinRM/WMI | `Microsoft.Management.Infrastructure` (CIM over **WS-Man/WinRM**, falling back to **DCOM**). This is the same stack PowerShell's `Get-CimInstance` uses. `System.Management` is kept only for the local machine. |
| D5 | vSphere SOAP | A **hand-written minimal SOAP client** (`HttpClient` + XML) for the ~15 calls we need (`RetrievePropertiesEx`, `QueryPerf`, `CreateContainerView`, alarms, events). This avoids WCF codegen and trimming problems in a single-file exe. Inventory also uses the vSphere Automation REST API. |
| D6 | Storage split | Three SQLite files in WAL mode under `C:\ProgramData\NetWatch\data\`: **`netwatch.db`** (config, alerts, audit; small, backed up often), **`metrics.db`** (time series), **`events.db`** (syslog, traps, Windows/DC events). This spreads out write contention and keeps config backups small. |
| D7 | Metrics store | EF Core is used for config, inventory, alerts and events. Time series go through a dedicated **`IMetricStore`** that uses raw ADO.NET with prepared bulk inserts, a single batched writer, and **date-sharded tables** (daily raw, monthly 5-min, yearly 1-h), so retention is `DROP TABLE` instead of deleting millions of rows. A PostgreSQL implementation can use native partitioning behind the same interface. |
| D8 | Auth | Cookie auth with a **server-side session table**, so idle timeout and revocation work. Passwords are hashed with PBKDF2 (ASP.NET Core Identity `PasswordHasher`, 600k iterations, SHA-256). An initial admin password is generated at install and written once to a file that only Administrators can read. |
| D9 | Sounds | Alert tones are synthesised with the Web Audio API, so there are no audio assets. Browsers block audio until the user interacts with the page once, so the UI shows an "Enable sound" button. NOC mode remembers the setting. |
| D10 | 4624 volume | Successful logons are stored but **exclude computer accounts (`*$`) by default**, and logon-type filters are configurable. Without this, 4624 events would make up most of `events.db`. |
| D11 | Time | All timestamps are stored in UTC (`INTEGER` unix-ms in metrics, `TEXT` ISO-8601 in EF tables). The UI formats them with `Intl.DateTimeFormat(..., { timeZone: 'Asia/Karachi' })`. PKT has no DST. |

### Unknowns (prompt FILL-INs) and the defaults I will use if you don't specify

| FILL-IN | Default assumption |
|---------|-------------------|
| Switch vendors | **Confirmed: Cisco, HP, Juniper.** Cisco IOS/IOS-XE/NX-OS (CISCO-PROCESS-MIB, CISCO-MEMORY-POOL-MIB / CISCO-ENHANCED-MEMPOOL-MIB, CISCO-ENVMON-MIB), HP/HPE ProCurve and ArubaOS-CX (HP-ICF-CHASSIS / STATISTICS MIBs, ARUBAWIRED-*), Juniper Junos (JUNIPER-MIB jnxOperatingTable for CPU/memory/temperature, jnxFruTable for fans/PSU). Vendor MIB OIDs are hard-coded (no MIB files needed); ENTITY-SENSOR-MIB / HOST-RESOURCES-MIB are the fallback. |
| SNMP version | Both **v2c and v3** (authPriv SHA/AES-128), chosen per credential profile. |
| Server OS mix | Windows Server 2016–2022, plus some RHEL/Ubuntu Linux. |
| vSphere | 7.0 / 8.0 **with vCenter**. A direct-ESXi mode is also supported. |
| DC count | 2–4 (no design impact). |
| Working hours | Sun–Thu or Mon–Fri? → default **Mon–Fri 08:00–17:00 PKT**, editable. |
| SMTP | Optional; disabled until configured in Settings. |

---

## 1. Solution / folder structure

```
NetWatch.sln
global.json                     # pins the SDK version
Directory.Build.props           # TFM, nullable, warnings-as-errors, version stamping
Directory.Packages.props        # central NuGet version management (one place to audit)
nuget.config                    # online feed + optional local offline folder feed
build.ps1                       # restore → test → npm build → publish single-file → ISCC
src/
  NetWatch.Core/                # domain entities, enums, value objects, interfaces. No infrastructure dependencies.
    Devices/  Metrics/  Alerts/  Events/  Security/  Abstractions/
  NetWatch.Data/                # EF Core DbContexts + migrations, IMetricStore (SQLite), retention, backup/restore
    Config/ (NetWatchDbContext)  Events/ (EventsDbContext)  Metrics/ (SqliteMetricStore, rollup jobs)
  NetWatch.Collectors/          # everything that talks to devices
    Scheduling/ (PollScheduler, concurrency gates)
    Icmp/  Snmp/ (poller, OID maps per vendor, trap listener)  Syslog/
    Windows/ (CIM, services, event logs)  Linux/ (SSH)  VMware/  ActiveDirectory/
    Discovery/  Simulator/      # simulator = fake devices/metrics/events for UI testing
  NetWatch.Alerts/              # rule evaluation, lifecycle state machine, dedup/flap, dependencies,
    Rules/  Engine/  Suppression/  Notifiers/ (SignalR, SMTP, EventLog, syslog-forward, script)
  NetWatch.Web/                 # ASP.NET Core: REST controllers, SignalR hubs, auth, embedded static UI
    Api/  Hubs/  Auth/  Embedded/
    ClientApp/                  # React + TypeScript + Vite → dist/ embedded as resources
      src/ api/ components/ pages/ hooks/ theme/ charts/
  NetWatch.Service/             # Program.cs: Windows service host, Kestrel/HTTPS, Serilog, cert bootstrap, CLI verbs
installer/
  NetWatch.Installer/           # netwatch.iss (Inno Setup), firewall/service helper scripts
tests/
  NetWatch.Tests/               # xUnit: alert engine, parsers (syslog, traps, event XML), rollups, counters
docs/                           # install, admin, ports, permissions, audit policy, backup, troubleshooting
offline/                        # (git-ignored) nuget/ and npm-cache/ for building inside the air gap
```

Runtime layout on the target:
```
C:\Program Files\NetWatch\NetWatch.Service.exe     (single file, self-contained)
C:\ProgramData\NetWatch\
  appsettings.json  data\*.db  logs\  certs\  backups\  reports\  scripts\
```

The service exe also has **CLI verbs** for support work: `NetWatch.Service.exe --reset-admin`, `--backup <path>`, `--restore <path>`, `--simulate` (simulator mode), `--console` (run in the foreground).

---

## 2. Database schema

Conventions: `Id INTEGER PK AUTOINCREMENT`, `*Utc` columns are UTC, soft references via FK with `ON DELETE` rules shown, enums stored as `TEXT` (readable, safe across refactors). The EF model avoids SQLite-only features so it ports to PostgreSQL.

### 2.1 `netwatch.db`: configuration, inventory, alerts, security

**Users and security**

| Table | Columns |
|-------|---------|
| `Users` | Id, Username (unique, case-insensitive), DisplayName, Email?, PasswordHash? (null for LDAP users), AuthSource (`Local`/`Ldap`), Role (`Admin`/`Operator`/`Viewer`), IsEnabled, FailedLoginCount, LockedUntilUtc?, LastLoginUtc?, MustChangePassword, CreatedUtc, UpdatedUtc |
| `UserSessions` | Id (random 256-bit token hash), UserId → Users (cascade), CreatedUtc, LastSeenUtc, ExpiresUtc, ClientIp, UserAgent, RevokedUtc? |
| `LdapRoleMappings` | Id, GroupDn, Role, Priority |
| `AuditLog` | Id, TimestampUtc, UserId?, Username, Action (`Login`, `LoginFailed`, `DeviceCreated`, `AlertAcknowledged`, …), TargetType?, TargetId?, DetailsJson?, ClientIp. Index (TimestampUtc), (UserId) |

**Inventory**

| Table | Columns |
|-------|---------|
| `Groups` | Id, ParentId? → Groups (restrict), Name, Kind (`Site`/`Building`/`Floor`/`Rack`/`Logical`), SortOrder. Unique (ParentId, Name) |
| `CredentialProfiles` | Id, Name (unique), Kind (`SnmpV2c`/`SnmpV3`/`Windows`/`Ssh`/`VCenter`/`Ldap`), DisplayHint (e.g. `user: svc-netwatch`, never the secret), SecretBlob (DPAPI LocalMachine + entropy; encrypted JSON of community / authProto / authKey / privProto / privKey / password / private key), CreatedUtc, UpdatedUtc. The API never returns `SecretBlob`; writing a new secret replaces it. |
| `Devices` | Id, Name, IpAddress (unique), Hostname?, Type (`Switch`/`Server`/`VM`/`DomainController`/`Hypervisor`/`VCenter`/`Other`), Os? (`Windows`/`Linux`/`NetworkOs`/`Esxi`), Vendor?, Model?, SerialNumber?, Firmware?, SysObjectId?, SysName?, Location?, GroupId? → Groups (set null), PollIntervalSec (default 60), IsEnabled, MaintenanceMode, MaintenanceUntilUtc?, ParentDeviceId? → Devices (set null; used for dependency suppression), Status (`Unknown`/`Up`/`Warning`/`Down`/`Maintenance`), StatusSinceUtc, LastPollUtc?, LastUptimeSec?, ConsecutiveFailures, Notes?, CreatedUtc, UpdatedUtc. Index (GroupId), (Type), (Status) |
| `DeviceTags` | DeviceId → Devices (cascade), Tag. PK (DeviceId, Tag) |
| `DeviceCredentials` | DeviceId → Devices (cascade), CredentialProfileId → CredentialProfiles (restrict), Purpose (`Snmp`/`Management`/`Vcenter`). PK (DeviceId, Purpose) |
| `Interfaces` | Id, DeviceId → Devices (cascade), IfIndex, Name, Description?, Alias?, IfType, SpeedBps, Mac?, AdminStatus, OperStatus, LastChangeUtc?, IsImportant (only these raise port-down alerts), IsMonitored, LastSeenUtc. Unique (DeviceId, IfIndex) |
| `Sensors` | Id, DeviceId → Devices (cascade), Kind (`Temperature`/`Fan`/`PowerSupply`/`Cpu`/`Memory`), SourceKey (OID index), Name, Unit?, LastValue?, LastState (`Ok`/`Warning`/`Critical`/`Unknown`), LastSeenUtc |
| *Phase 2* `Volumes` | Id, DeviceId, Name/MountPoint, TotalBytes, LastFreeBytes, LastSeenUtc |
| *Phase 2* `WatchedServices` | Id, DeviceId, ServiceName, DisplayName?, ExpectedState (`Running`), LastState, LastSeenUtc |
| *Phase 3* `VmInventory`, `Datastores`, `VmSnapshots` | VM→Host→Datastore mapping, snapshot age/size |

**Alerting**

| Table | Columns |
|-------|---------|
| `AlertRules` | Id, Name, IsEnabled, IsBuiltIn, RuleType (`Threshold`/`StateChange`/`EventMatch`/`CountInWindow`/`Absence`), Severity (`Info`/`Warning`/`Major`/`Critical`), ScopeJson (device ids / group ids / types / tags; empty = all), MetricKey? (e.g. `cpu.pct`, `iface.util.in`), Operator? (`>`,`>=`,`<`,`<=`,`==`,`!=`), Threshold?, ClearThreshold? (hysteresis), DurationSec?, WindowSec?, CountThreshold?, GroupByField? (e.g. `TargetUser`), EventFilterJson?, EscalateAfterMin?, ChannelsJson, CreatedUtc, UpdatedUtc |
| `Alerts` | Id, RuleId? → AlertRules (set null), DeviceId? → Devices (set null), InterfaceId?, DedupKey (rule+entity), Severity, State (`Active`/`Acknowledged`/`Resolved`/`Closed`), Title, Message, CurrentValue?, FirstSeenUtc, LastSeenUtc, OccurrenceCount, FlapCount, AckByUserId?, AckUtc?, AckComment?, ResolvedUtc?, ClosedUtc?, ClosedByUserId?, SuppressedReason? (`Maintenance`/`ParentDown`/`Flapping`), EscalatedUtc?. **Filtered unique index on DedupKey where State in (Active, Acknowledged)** → dedup. Index (State, Severity), (DeviceId) |
| `AlertHistory` | Id, AlertId → Alerts (cascade), TimestampUtc, Action (`Raised`/`Escalated`/`Acknowledged`/`Commented`/`Resolved`/`Reopened`/`Closed`), UserId?, Comment? |
| *Phase 2* `MaintenanceWindows` | Id, Name, ScopeJson, StartUtc, EndUtc, CronRecurrence?, DurationMin?, Reason, CreatedByUserId |
| `Settings` | Key (PK), ValueJson, UpdatedUtc, UpdatedByUserId? (thresholds, retention, SMTP, theme defaults, working hours) |

### 2.2 `metrics.db`: time series (raw ADO, sharded, `WITHOUT ROWID`)

Load estimate: 6,000 interfaces × 1 row/min = **8.6 M rows/day**. A narrow `(series, ts, value)` design would need about 8 values per interface (~70 M rows/day, roughly 15 GB per 7 days). A **wide row per interface** keeps the 7-day raw window at about 3–4 GB.

| Table (shard suffix) | Columns | Retention |
|---|---|---|
| `if_raw_YYYYMMDD` | InterfaceId, Ts (unix ms), InBps, OutBps, InUtilPct, OutUtilPct, InErrPs, OutErrPs, InDiscPs, OutDiscPs, OperUp. PK (InterfaceId, Ts) | 7 days |
| `dev_raw_YYYYMMDD` | DeviceId, Ts, Up, RttMs, LossPct, CpuPct, MemPct, TempC. PK (DeviceId, Ts) | 7 days |
| `gen_raw_YYYYMMDD` | SeriesId, Ts, Value. PK (SeriesId, Ts). For everything else (per-volume disk %, VM cpu ready, sensors…) | 7 days |
| `Series` | Id, DeviceId, Key (e.g. `disk.used.pct`), Instance (e.g. `C:`), Unit. Unique (DeviceId, Key, Instance) | n/a |
| `if_5m_YYYYMM`, `dev_5m_YYYYMM`, `gen_5m_YYYYMM` | same keys + Avg/Max (Min for gen) per value, SampleCount | 90 days |
| `if_1h_YYYY`, `dev_1h_YYYY`, `gen_1h_YYYY` | same as 5m | 1–2 years |
| `CounterState` | InterfaceId PK, Ts, InOctets, OutOctets, InErr, OutErr, InDisc, OutDisc, SysUptime. Used to compute rates across service restarts and to handle counter wraps or device reboots. | n/a |

Queries choose the resolution from the requested range: ≤ 24 h → raw, ≤ 30 d → 5 m, more → 1 h. A rollup job runs every 5 min and every hour, and idempotently re-aggregates the last closed bucket.

### 2.3 `events.db`: high-volume events (EF, indexed by time)

| Table | Phase | Key columns |
|---|---|---|
| `DeviceEvents` | 1 | Id, TimestampUtc, DeviceId?, Kind (`StatusChange`/`Reboot`/`InterfaceChange`/`Discovery`), Severity, Message |
| `SyslogMessages` | 2 | Id, ReceivedUtc, SourceIp, DeviceId?, Facility, Severity, Hostname?, AppName?, Message, Raw |
| `SnmpTraps` | 2 | Id, ReceivedUtc, SourceIp, DeviceId?, TrapOid, TrapName?, VarbindsJson |
| `WindowsEvents` | 2 | Id, TimestampUtc, DeviceId, LogName, Provider, EventId, Level, RecordId, Message. Unique (DeviceId, LogName, RecordId) |
| `DcSecurityEvents` | 4 | Id, TimestampUtc, DcDeviceId, EventId, RecordId, SubjectUser, TargetUser, TargetDomain, GroupName?, SourceIp?, Workstation?, LogonType?, Status?/SubStatus?, ResultCode?, ObjectDn?, AttributeName?, ExtraJson. Indexes (TimestampUtc), (EventId, TimestampUtc), (TargetUser), (SourceIp) |
| `ReplicationStatus` | 4 | DcDeviceId, Partner, NamingContext, LastSuccessUtc, LastAttemptUtc, LastResult, ConsecutiveFailures |
| `EventBookmarks` | 2 | DeviceId, LogName, LastRecordId, so that remote event-log reads resume without gaps or duplicates |

Events are purged nightly in batches of 50k rows (default 365 days) so the writer is not blocked.

---

## 3. Packages and licenses

All packages are restored at **build time** only. Nothing contacts the internet at runtime: no telemetry, update checks or license calls. The `build.ps1 -PrepareOffline` mode copies the NuGet global-packages folder and the npm cache into `offline/` so the solution can be rebuilt inside the air gap.

### 3.1 NuGet (backend)

| Package | License | Purpose | Phase |
|---|---|---|---|
| Microsoft.Extensions.Hosting.WindowsServices | MIT | Run as a Windows service | 1 |
| Microsoft.EntityFrameworkCore.Sqlite (+ .Design, build-only) | MIT | ORM / migrations | 1 |
| Microsoft.Data.Sqlite (bundles SQLitePCLRaw e_sqlite3) | MIT / Apache-2.0 | Metrics store, bulk inserts | 1 |
| Microsoft.Extensions.Identity.Core | MIT | PBKDF2 `PasswordHasher` | 1 |
| System.Security.Cryptography.ProtectedData | MIT | DPAPI credential encryption | 1 |
| Lextm.SharpSnmpLib | MIT | SNMP v1/v2c/v3 get/walk/bulk, trap listener | 1 / 2 |
| Serilog.AspNetCore, Serilog.Sinks.File, Serilog.Settings.Configuration | Apache-2.0 | Rolling file logging | 1 |
| Cronos | MIT | Cron expressions (rollups, retention, backup, reports) | 1 |
| Microsoft.Management.Infrastructure | MIT | CIM over WinRM / DCOM | 2 |
| System.Diagnostics.EventLog | MIT | Remote `EventLogSession`, write to local Event Log | 2 |
| SSH.NET | MIT | Linux polling | 2 |
| MailKit | MIT | SMTP (optional) | 2 |
| System.DirectoryServices, .Protocols, .AccountManagement | MIT | LDAP login, AD replication API | 2 (LDAP) / 4 |
| PDFsharp-MigraDoc (6.x) | MIT | Offline PDF reports | 5 |
| ScottPlot (5.x, uses SkiaSharp MIT) | MIT | Chart images inside PDF reports | 5 |
| CsvHelper | MS-PL / Apache-2.0 | CSV import/export | 1 (import) / 5 |
| **Tests:** xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | Apache-2.0 / MIT | Unit tests | 1 |

**Deliberately avoided:** QuestPDF (its Community license has a revenue limit), FluentAssertions v8+ (commercial license), Moq (SponsorLink history), Quartz.NET (not needed).
Built into the ASP.NET Core shared framework, so no package is needed: Kestrel, SignalR server, cookie auth, data protection, rate limiting, and `CertificateRequest` for the self-signed certificate.

### 3.2 npm (frontend, build-time only, output embedded in the exe)

| Package | License | Purpose |
|---|---|---|
| react, react-dom (18.3.x) | MIT | UI |
| react-router-dom | MIT | Routing |
| @tanstack/react-query | MIT | API cache / refetch |
| @tanstack/react-table, @tanstack/react-virtual | MIT | Fast 500-device / 6,000-interface tables |
| @microsoft/signalr | MIT | Live updates |
| echarts (5.x) | Apache-2.0 | Graphs, gauges, topology (graph series). Uses a ~40-line wrapper instead of `echarts-for-react`. |
| lucide-react | ISC | SVG icons (tree-shaken, bundled) |
| **dev:** typescript (Apache-2.0), vite + @vitejs/plugin-react (MIT), vitest (MIT), eslint (MIT) | | Build / test |

No UI kit and no web fonts. The UI uses its own CSS with design tokens and a system font stack (`Segoe UI`, `system-ui`), so nothing is fetched. A Vite build check fails the build if any `http(s)://` URL appears in the output bundle.

### 3.3 Tooling (build machine only)

| Tool | License |
|---|---|
| .NET 10 SDK | MIT |
| Node.js 20+/24 | MIT |
| Inno Setup 6 | Inno Setup License (free, commercial use allowed) |

---

## 4. Phase 1 scope (what "done" means)

1. Service host: runs as a Windows service or a console app, uses Serilog, generates a self-signed certificate on first run (SAN = hostname + IPs) or loads a PFX, serves HTTPS on 8443 and redirects any HTTP traffic.
2. DB bootstrap: the three SQLite files in WAL mode, with automatic EF migrations at start-up (needed for the upgrade path).
3. Auth: login page, local users, roles (Admin/Operator/Viewer), session timeout, lockout after N failed logins, audit trail, user management page. LDAP login: moved to early Phase 2 (decided during Phase 1).
4. Inventory: groups tree, credential profiles (DPAPI), devices CRUD, CSV import, subnet discovery (ICMP sweep + SNMP sysObjectID/sysDescr probe), auto-detect vendor/model/serial/firmware.
5. Polling: ICMP (RTT/loss, 3 echoes), SNMP system (sysUpTime reboot detection), IF-MIB HC counters → bps/util/errors/discards, CPU/mem/temp for Cisco/HP/Juniper plus fallback. Concurrency limits, jitter, and bulk writes through the single writer.
6. Metrics: raw + 5 m + 1 h rollups, retention jobs, range-adaptive queries.
7. UI: shell (sidebar, dark/light theme), Overview, Devices list + device detail (live + 1h/24h/7d/30d/custom graphs, interfaces table with "important" toggle), Switch port grid, Alerts (active + history, acknowledge), Settings (devices, credentials, users, thresholds).
8. Alerts: threshold + state-change rules with the default thresholds from §10. Lifecycle is Active → Ack → Resolved (auto), with dedup. Live SignalR pop-up, banner and sound in every browser.
9. Simulator mode (`--simulate`): 120 fake switches, 20 servers, 40 VMs, with realistic traffic curves and random faults.
10. Tests: alert engine (threshold duration, hysteresis, dedup), counter-to-rate math (wrap and reboot cases), SNMP vendor detection, CSV import parsing.
11. `build.ps1` + Inno Setup installer: install, upgrade in place (stop service → replace exe → start, keeping ProgramData), uninstall with a "delete data" checkbox (unchecked by default), firewall rule for 8443 (162/514 are added in Phase 2 together with the trap/syslog receivers), Start-menu shortcut.

---

## 5. Phase 2 scope (delivered in 1.1.0)

Decisions made while building Phase 2 (they refine sections 0–3):

| Topic | Decision |
|---|---|
| WinRM client | The Windows WinRM client (`WSMan.Automation` COM object) instead of `Microsoft.Management.Infrastructure`: it is part of every Windows Server, works in a single-file exe and handles Kerberos/NTLM message encryption on port 5985. WMI over DCOM (`System.Management`) is the fallback. |
| Event logs | Remote `EventLogSession` (RPC) with XPath queries on System/Application, Critical+Error (Warning optional), incremental by record-ID bookmark stored in `events.db` (`EventBookmarks`). First contact starts at the newest record. |
| Linux | SSH.NET; one read-only command (`/proc`, `df`, `systemctl show`); host key pinned on first use (`Devices.SshHostKeyFingerprint`). |
| Server metrics | Device CPU/memory stay in `dev_*`; per-volume disk %, load per CPU and network throughput go to a new generic series store (`Series` registry + `gen_raw/5m/1h` shards, same rollups and retention). |
| New tables | `ServerInfo`, `Volumes`, `WatchedServices`, `MaintenanceWindows` (netwatch.db); `SyslogMessages`, `SnmpTraps`, `WindowsEvents`, `EventBookmarks` (events.db). Rules gained `FilterJson`, `WindowSec`, `CountThreshold`, `EscalateAfterMin`; alerts gained `IsFlapping`, `FlapCount`, `EscalatedUtc`. |
| Receivers | Syslog UDP/TCP 514 (RFC 6587 framing) and traps UDP 162 (SharpSnmpLib, v3 users from credential profiles) feed one batched writer with a global rate limit. |
| Alert lifecycle | Rule types EventMatch (filters), CountInWindow, Absence; flapping hold; escalation; dependency suppression via the parent chain (a parent counts as unreachable from its first failed poll); maintenance windows evaluated by the inventory cache every 30 s (cron in Asia/Karachi via Cronos). |
| Notifications | Windows Event Log (source registered by the installer), RFC 5424 syslog forward, scripts restricted to `ProgramData\NetWatch\scripts` (data via environment/stdin, never the command line), SMTP via MailKit with digest for bursts. |
| LDAP | `System.DirectoryServices.Protocols`, bind as the user (Negotiate or simple bind over TLS), nested group check with `LDAP_MATCHING_RULE_IN_CHAIN`, highest mapped role wins, role refreshed at each sign-in. |
| Packages added | System.Management, System.Diagnostics.EventLog, System.DirectoryServices.Protocols (MIT, Microsoft), SSH.NET (MIT), MailKit (MIT). All offline at run time. |
## 6. Phase 3 scope (delivered in 1.2.0)

| Topic | Decision |
|---|---|
| vSphere client | Hand-written vim25 SOAP client (`HttpClient` + XML, about 10 calls: RetrieveServiceContent, Login/Logout, CreateContainerView, RetrievePropertiesEx/ContinueRetrievePropertiesEx, QueryPerf, CreateCollectorForEvents/ReadNextEvents/DestroyCollector). No VMware SDK, nothing to download, works in the single-file exe. The vSphere Automation REST API was not needed: one property-collector query returns the full inventory. |
| Polling | A device of type vCenter (or Hypervisor) with a credential of purpose VCenter is polled through the normal scheduler; the vSphere query runs beside ping/SNMP. |
| Storage | `VmwareHosts`, `VmwareVms`, `VmwareDatastores`, `VmwareSnapshots`, `VmwareAlarms` (netwatch.db, current state, replaced each poll); `VmwareEvents` (events.db, bookmark by event key). Host CPU/memory, datastore use and VM CPU ready go to generic series. |
| Device mapping | VMs and hosts are matched to inventory devices by IP, then name (also the short name of an FQDN); their alerts are raised on that device. |
| Snapshot size | Approximate: the snapshot's state/memory files plus delta disks in the next point's chain that are not in its own chain. |
| Certificates | CA-trusted certificates are accepted; otherwise the SHA-256 thumbprint is pinned on first use (`Devices.TlsThumbprint`) and verified afterwards. |
| Rules | 15 built-in VMware rules (API unreachable, host not connected/hardware/CPU/memory, datastore space/accessibility, old/large snapshot, CPU ready, Tools, vCenter alarms red/yellow, major events). |

## 7. Phase 4 scope (delivered in 1.3.0)

| Topic | Decision |
|---|---|
| Collection | A device of type Domain controller with a Windows credential gets a DC request beside the normal server request. Security log via remote `EventLogSession` (XPath on the collected event IDs, resumed by `EventRecordID` bookmark; first contact starts at the newest record); Directory Service and DFS Replication logs (level 1–3); topology and replication over LDAP 389 with Negotiate signing + sealing (rootDSE, `fSMORoleOwner`, `msDS-NCReplInboundNeighbors` per naming context) every `ReplicationCheckMin`; clock via WMI `Win32_UTCTime` on the DC and on the PDC emulator (offset measured at the midpoint of the query, 1 s resolution). No `System.DirectoryServices` (ADSI) dependency. |
| WEF | Optional `ForwardedEventsReader`: reads this server's `ForwardedEvents` log every 30 s, matches the event's computer name to a DC device, and stores events through the same path. Forwarded events keep their original record ID, so the unique (DC, record) index removes duplicates; after a restart the last hour is re-read. Remote Security-log reads are skipped while WEF is on. |
| Parsing | `DcEventParser` maps `EventRecord.ToXml()` (EventData and UserData) to user, domain, target, group (member DN → CN), object, source IP (`::ffff:` stripped), workstation, logon type, status/sub-status and a failure flag; XML DTDs prohibited. Pure functions, unit tested. |
| Storage | `DcSecurityEvents` (events.db, indexes on time, event ID + time, target user, source IP, unique DC + record), `DcInfo`, `DcReplicationLinks` (unique DC + partner + naming context), `SavedSearches` (netwatch.db). DS/DFSR entries go to `WindowsEvents`. Clock skew is a generic series. Retention as for other events. |
| Filters | 4624/4768/4776 successes are stored (logon types 2, 7, 10, 11 by default) but not sent to the alert engine; computer accounts and service identities are dropped where they are noise. |
| Alert engine | `EventSignal` gained `Fields`; `EventFilter` gained `FieldEquals`; CountInWindow rules gained `GroupByField` (one counter, dedup key and alert per field value, e.g. per account or source). The processor sets `failed`, `privileged` (configurable group list), `offHours` (working days/hours in PKT), `targetUser`, `source`, `sourceIp`, `group`, `logonType`. |
| Rules | 13 built-in DC rules (failed logons per account ≥ 5 / per source ≥ 20 in 5 min, lockout, privileged group add, log cleared, audit policy change, off-hours admin logon, new user/computer account, replication failing 15 min, DS/DFSR replication events, clock skew > 30 s / > 240 s). |
| UI | `/dc` page: overview, security event viewer with saved searches (own; shared by operators), lockout tracker, privileged group change log, replication matrix, recommended audit policy. Settings → General → Domain controllers. |
| Simulator | SIM-DC-01 (PDC) and SIM-DC-02 with office-hours logon traffic, brute force → lockout, password spray, rare account/group changes, admin logons at odd hours, replication failures (1722) and clock drift on the non-PDC. |

## 8. Phase 5 scope (delivered in 1.4.0)

| Topic | Decision |
|---|---|
| Reports | Six types (availability, alert history, interface utilization, capacity, DC security, inventory). `ReportBuilder` produces a format-neutral document (sections with key figures, bar lists and tables); renderers write PDF or CSV. Availability is the mean of the stored `Up` series (raw/5-minute/hourly by period). At most two reports are built at once; on-demand runs are limited to 10 per minute per user. |
| PDF | Own minimal PDF 1.4 writer (`PdfWriter`): A4, the standard Helvetica/Helvetica-Bold Type 1 fonts with their AFM widths for layout (no font embedding, no third-party library, nothing to license or download), WinAnsi text, Flate-compressed content streams, page breaks with repeated table headers, "page n of N" footers. |
| CSV | CsvHelper, UTF-8 with BOM, one block per section; values starting with = + - @ (non-numeric) are prefixed with ' against spreadsheet formula injection. |
| Schedules | `ReportSchedules` table (netwatch.db, Phase5 migration): daily/weekly/monthly at a PKT time, covering the previous day / 7 days / calendar month. `ReportScheduler` checks every minute, saves to `reports\YYYY-MM\` (400-day retention) and e-mails through the existing SMTP channel (MailKit, attachment up to 15 MB). |
| Backups | `BackupService`: `VACUUM INTO` per database (consistent online snapshot), appsettings.json, certs, manifest with SHA-256 per file, one zip; daily at a PKT time, newest N kept, optional external folder (write-tested on save). Restore is offline only (`--restore`): manifest and checksums verified, entries allow-listed (no zip slip), current files moved to `before-restore-…`. A named mutex per data folder prevents two instances and restoring under a running service. |
| Self-monitoring | `SelfMonitorService` evaluates pure `SelfChecks` every minute (disk, polling backlog for 3 min, dropped samples/signals, receiver drops, certificate expiry, backup age/failure, report failures, notification errors, WEF) and publishes them as `netwatch.selfcheck` states of an auto-created "<server> (NetWatch)" device (127.0.0.1), so the normal alert/notification path applies (rule "NetWatch self-check failing", Major, 2 min). |
| NOC / topology | `/noc` renders outside the normal layout: live counts, open alerts, rotating panels (problems, sites, busiest links, DCs). `/topology` uses the ECharts tree series on the parent links (`/api/topology`), collapsed large branches coloured by their worst status. |
| Hardening | Rate limit for heavy endpoints, `Cross-Origin-Resource-Policy`, backup downloads audited, report/backup file access by strict name patterns and path checks, report e-mail recipients validated, saved-search page length aligned with the schema. |

## 9. 2.0 scope (delivered in 2.0.0)

The individual decisions, with reasons, are in [decisions.md](decisions.md) (31 entries). Summary:

| Area | Design |
|---|---|
| Start-up safety | `--self-test` boots the full host and stops; a unit test runs it on an empty data folder, and `HasPendingModelChanges()` guards both EF models. Configuration order: defaults → `appsettings.json` → `NETWATCH_*` environment → command line |
| DC collection | Remote Security-log reads or WEF per DC (`ForwardedDcs`), bookmark per log, exponential back-off (1–30 min) for failing reads, 4624 ignore-sources (IP/CIDR/workstation). New rules: Kerberos spike, account deleted. Health: replication stale (`ReplicationStaleHours`), TCP checks of 389/88/53 every poll (`DcInfo.ReachabilityJson`) |
| Switch ports | `dot3StatsDuplexStatus` and `ifLastChange` read every poll; `FlapTracker` counts status changes and hidden bounces; `NetInterface.Duplex`, `ExpectedSpeedBps`, `FlapCount24h`; half-duplex and below-speed checks on important ports |
| Topology | LLDP-MIB and CISCO-CDP-MIB read with the interface table (`NeighborLinks`, matched to devices by IP then name); `TopologySuggester` walks the link graph from the core to suggest upstream devices (never through servers) |
| Vendor health | `SnmpHealthProfile` (name/state/value columns with state mappings) — built-in ArubaOS-CX, overridable in `snmp-profiles.json`; HOST-RESOURCES/ENTITY-SENSOR remain the generic fallback |
| GPUs | Win32_VideoController + GPU Engine/Adapter Memory counters; `nvidia-smi` over a WinRM remote shell (WS-Man Shell through WSMan.Automation) or SSH with lspci; `ServerGpus` table; metrics per GPU |
| iLO / iDRAC | `HardwareCollector`: Redfish (basic auth, pinning, classic Thermal/Power, Storage/Drives) or SNMP overall health; `HardwareComponents` table replaced per read, every 5 min |
| Reports | VMware report; `CapacityMath` (linear days-until-full, half-period trend); preview endpoint returns the format-neutral document |
| Backups | `PortableSecrets` (PBKDF2-SHA256 600k + AES-256-GCM per secret, check value); `PendingRestore` marker → restore at next start before any DB is opened; installer `failureflag` so exit code 75 restarts the service |
| NOC | `KioskDisplays`: hashed link token → hidden Viewer account (AuthSource Kiosk) with a persistent cookie and no idle timeout |
| Pilot tools | `ConnectionTestService` (10 tests, steps with exact errors, last 100 kept); `DiagnosticBundleService` (logs, tests, stats, redacted config, `snmpwalk -On` walks) |
| TLS | `TlsPolicy`: revocation NoCheck/Offline, no downloads, applied to vCenter, Redfish, LDAPS (own verification incl. host name) and SMTP; console certificate import (`ConsoleCertificateStore`) |
| Build | NuGet lock files (win-x64 graph) with locked restore; `npm ci`; optional signtool signing; `npm test` and `dotnet test` in `build.ps1` |
| Tests | 265 .NET tests (unit, sample-data parsers, real-host HTTPS and SignalR integration, endpoint-authorization enumeration) and 11 UI tests |

New tables / columns (migrations Wp3DcHealth, Wp5SwitchPorts, Wp6Hardware, Wp9Kiosk): `DcInfo.ReachabilityJson`,
`Interfaces.Duplex/ExpectedSpeedBps/FlapCount24h`, `NeighborLinks`, `Devices.BmcAddress/BmcTlsThumbprint`,
`ServerGpus`, `HardwareComponents`, `KioskDisplays`. New packages: none.
