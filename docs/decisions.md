# NetWatch 2.0 decisions log

Decisions taken while completing 2.0 where the brief was ambiguous or where the code already went another way.
Each entry: what was decided, and why.

| # | Date | Topic | Decision | Why |
|---|---|---|---|---|
| 1 | 2026-11-21 | Starting point | 2.0 work starts from 1.4.0 (Phases 4 and 5 were committed after the 68 % status report), not from 1.2.0. Each work package fills the gaps between 1.4.0 and the 2.0 brief instead of re-implementing what exists. | Avoids duplicate code and keeps 1.3/1.4 data compatible. |
| 2 | 2026-11-21 | Configuration precedence | defaults → data-folder `appsettings.json` → `NETWATCH_*` environment variables (`NETWATCH_Https__Port`) → command line (`--Https:Port=8444`). | The settings file used to be added last, so it silently overrode the command line. |
| 3 | 2026-11-21 | Start-up test | `--self-test` starts the full host (databases, seeding, hosted services, Kestrel), runs `SelfTest:Seconds` (default 5), stops, exits 0. The unit test runs it on an empty temp folder with `--simulate --Https:Port=0`, and two tests call `HasPendingModelChanges()` on both EF contexts. | Catches a missing migration (the 1.2.0 working-tree crash) and any start-up exception in CI. The same switch can be used after installation. |
| 4 | 2026-11-21 | Version | `2.0.0-dev` during development. Inno Setup's `VersionInfoVersion` must be numeric, so `build.ps1` passes `/DNumericVersion` (the version without its pre-release suffix). | Inno Setup rejects `2.0.0-dev` there. |
