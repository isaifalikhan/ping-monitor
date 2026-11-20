# NetWatch (offline edition)

Agentless infrastructure monitoring for an air-gapped network: one Windows service, one installer, no internet
access at run time (no CDN, no telemetry, no update checks).

**Phase 1 (this release):** Windows service with its own HTTPS web console, login with roles (Admin / Operator /
Viewer), device inventory (manual, CSV import, subnet discovery), ICMP ping and SNMP v2c/v3 polling of switches
(Cisco, HP/Aruba, Juniper, plus standard-MIB fallbacks), interface traffic graphs, CPU/memory/temperature/sensor
health, threshold and state alerts with on-screen pop-ups and sound, and a Windows installer.

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

## Running locally without hardware

```powershell
dotnet build NetWatch.slnx
dotnet run --project src\NetWatch.Service -- --console --simulate --data-dir .data
```

Open https://localhost:8443/, sign in as `admin` with the one-time password from
`.data\initial-admin-password.txt`, and choose a new password. `--simulate` adds 120 switches, 20 servers and
40 VMs with realistic traffic, faults and reboots, so every screen and alert can be exercised.
