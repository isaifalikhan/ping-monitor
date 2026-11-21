# NetWatch (offline edition)

Agentless infrastructure monitoring for an air-gapped network: one Windows service, one installer, no internet
access at run time (no CDN, no telemetry, no update checks).

**Phase 1:** Windows service with its own HTTPS web console, login with roles (Admin / Operator / Viewer), device
inventory (manual, CSV import, subnet discovery), ICMP ping and SNMP v2c/v3 polling of switches (Cisco, HP/Aruba,
Juniper, plus standard-MIB fallbacks), interface traffic graphs, CPU/memory/temperature/sensor health, threshold and
state alerts with on-screen pop-ups and sound, and a Windows installer.

**Phase 2 (1.1.0):** agentless server monitoring (Windows via WinRM/WMI with DCOM fallback and remote
event logs; Linux via SSH; SNMP HOST-RESOURCES fallback) with disks, watched services, pending reboot, last patch,
load and network; SNMP trap receiver (v1/v2c/v3, informs) and syslog receiver (UDP/TCP, RFC 5424/3164 and vendor
formats) with search; full alert lifecycle (event filters, count-in-window and absence rules, flapping protection,
escalation, dependency suppression, scheduled maintenance windows); offline notification channels (Windows Event
Log, syslog forwarding, script, SMTP); sign-in with Active Directory accounts (LDAPS, group → role mapping).

**Phase 3 (this release, 1.2.0):** VMware monitoring through the vSphere API (vCenter or standalone ESXi): hosts,
VMs, datastores and snapshots (age and size), CPU ready, VMware Tools, triggered vCenter alarms and events, VM →
host → datastore mapping, a VMware page, VMware alert rules, and certificate pinning for self-signed vCenters.

The earlier cloud version (Next.js + Supabase) is preserved at git tag `legacy-cloud-netwatch`.

## Documentation

| Document | For |
|---|---|
| [docs/installation-guide.md](docs/installation-guide.md) | Installing, upgrading and uninstalling on the monitoring server; certificate; firewall |
| [docs/admin-guide.md](docs/admin-guide.md) | Daily administration: devices, credentials, alerts, users, retention, backup/restore, troubleshooting |
| [docs/netwatch-offline-design.md](docs/netwatch-offline-design.md) | Architecture, database schema, technology choices, phase plan |

## Repository layout

```
src/
  NetWatch.Core        domain model, settings, metric math (no dependencies)
  NetWatch.Data        SQLite: EF Core config + events DBs, sharded metric store, rollups, retention
  NetWatch.Alerts      alert evaluator (threshold / state / event), alert manager, built-in rules
  NetWatch.Collectors  poll scheduler, ICMP, SNMP client and vendor collectors, simulator
  NetWatch.Web         REST API, authentication, SignalR live hub, embedded React UI (ClientApp/)
  NetWatch.Service     Windows-service host (Kestrel HTTPS, Serilog, certificate, first-start seeding)
tests/NetWatch.Tests   xUnit tests
installer/             Inno Setup 6 script
build.ps1              one-command build: UI, tests, single-file exe, installer
```

## Building

Requirements on the build PC: .NET 10 SDK, Node.js 20+, Inno Setup 6.

```powershell
.\build.ps1                 # online build -> artifacts\NetWatch-Setup-1.0.0.exe
.\build.ps1 -PrepareOffline # fill offline\ with every NuGet/npm package (internet-connected PC)
.\build.ps1 -Offline        # build inside the air-gapped network from offline\
```

Unit tests: `dotnet test NetWatch.slnx`. On a PC with Windows **Smart App Control** enabled, freshly built unsigned
assemblies and executables can be blocked ("An Application Control policy has blocked this file"); build and test on
a build machine or server without it (Windows Server has no Smart App Control), or sign the binaries with your
organisation's code-signing certificate.

## Running locally without hardware

```powershell
dotnet build NetWatch.slnx
dotnet run --project src\NetWatch.Service -- --console --simulate --data-dir .data
```

Open https://localhost:8443/, sign in as `admin` with the one-time password from
`.data\initial-admin-password.txt`, and choose a new password. `--simulate` adds 120 switches, 20 servers (15
Windows, 5 Linux) and 40 VMs with realistic traffic, faults, reboots, filling disks, stopping services, Windows
event-log errors, syslog messages and SNMP traps, so every screen and alert can be exercised.
