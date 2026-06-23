# Changelog

## [Unreleased] - Phase 14: Automated tests

### Testing
- Unit tests for maintenance window logic (`maintenance/utils`)
- Settings enforcement: IP allowlist, session TTL, preference merge, retention purge
- Report CSV builder and filename helpers
- Monitor target resolution, cron maintenance helpers, executor type guards
- Role permission helpers and API rate limiting
- Zod schemas: settings, maintenance windows, monitor create API
- Jest coverage paths configured for `src/lib/**`

Run from repo root: `pnpm test`, `pnpm typecheck` (or `pnpm -C apps/frontend test`).

## [Unreleased] - Phase 13: Production deployment docs

### Documentation
- Expanded `apps/frontend/DEPLOYMENT.md` — Supabase migrations, Vercel cron, env vars, bootstrap, verification checklist, security, troubleshooting
- Updated root `README.md` for current Vercel + Supabase stack (legacy backend marked deprecated)
- Updated `.env.example` with all production variables and comments

## [Unreleased] - Phase 12: Security hardening

### Security
- Middleware: global API rate limits (auth/public/api tiers), `Cache-Control: no-store` on API responses
- Security headers on all routes (`X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy`, etc.)
- Production-safe API errors — generic messages in production, detailed in development
- Request body size cap (1 MB) on Zod-validated routes
- `429` responses include `Retry-After` header
- Per-route `guardApiRequest` helper for write/read rate limits

### Audit trail
- `logAuditFromRequest` captures client IP on security events
- Failed login attempts logged (SECURITY) when email matches a user
- IP allowlist blocks logged before 403
- Maintenance schedule/cancel events logged (MAINTENANCE category)
- Settings updates record actor user ID and IP
- Audit logs API restricted to Owner/Admin; UI gated with empty/loading states

## [Unreleased] - Phase 11: Settings functional and enforced

### Settings
- Dedicated `settings.ts` service with Zod validation on PATCH (`api-settings.ts`)
- Owner-only updates enforced on API (unchanged); non-owners see read-only UI banner
- **Enforced at runtime:**
  - Latency threshold → DEGRADED checks in cron
  - Default check interval / timeout → new monitor creation (API + create form)
  - Data retention → purges old `monitor_checks` each cron run
  - Session timeout → access/refresh token TTL on login
  - IP allowlist → blocked on authenticated API requests (403)
  - Public status page toggle → public status API
  - Status page slug → updates organization slug and `status_pages`
  - Notification email → fallback for email alert channels
  - Brand color/logo → public status page
- Settings UI documents which values are enforced vs stored for later

## [Unreleased] - Phase 10: Maintenance UI integration

### Maintenance
- Shared `maintenance/utils.ts` for window status, monitor scope, and alert-pause logic
- `maintenance.ts` service with Zod validation, list/create/cancel (delete upcoming or end active early)
- Monitors enriched with `inMaintenance`, `alertsPaused`, `maintenanceWindow`, and `displayStatus`
- `GET /api/maintenance/active`, `DELETE /api/maintenance/[id]`
- Dashboard and alerts banners when maintenance is active; monitor list/detail badges and filters
- Manual **maintenance mode** toggle on monitors (skips checks, pauses alerts)
- Maintenance page: cancel/end windows, scope column, checks-skipped indicator, `?monitorId=` form prefill
- Demo store resolves window status from dates and supports cancel

## [Unreleased] - Phase 9: Reports real data

### Reports
- New `reports.ts` service aggregates `monitor_checks`, `incidents`, and `monitors` for the selected date range
- Summary KPIs, daily response-time trend, daily incident counts, and per-monitor uptime (from checks in range)
- API response shape aligned with reports UI (`responseTrend`, `incidentTrend`, `monitors`, `period`)
- CSV export downloads summary, monitor uptime, and daily trend tables (template presets: 30/90 day ranges)
- PDF and Excel marked coming soon; demo store respects custom from/to filters
- Chart and table empty states when no data in period

## [Unreleased] - Phase 8: Dashboard real data

### Dashboard
- Stats, charts, and recent tables load from Supabase when `NEXT_PUBLIC_DEMO_MODE=false`
- New `dashboard.ts` service: aggregates `monitor_checks`, `incidents`, and `monitors` for KPIs and trends
- Response time trend (24h, 2h buckets), uptime trend (7d), uptime 24h, incident trend (7d)
- Category distribution pie chart (replaces synthetic type buckets); status includes Unknown
- Recent alerts join monitor names via `alert_logs.monitor_id`
- Chart and table empty states; dashboard skeleton while stats load
- Demo store aligned with category-based distribution

## [Unreleased] - Phase 7: Alert engine

### Alerts
- Production channels: **Email**, **Webhook**, **Slack** (Teams/Telegram/WhatsApp/SMS marked coming soon)
- Triggers: monitor down, recovery, high latency, SSL expiring, DNS failure
- Rules evaluated after each check; incident open/close dispatches MONITOR_DOWN / MONITOR_RECOVERY
- Failed deliveries retried once; errors logged with attempt count
- `alert_logs.monitor_id` links deliveries to monitors (migration `004_alert_logs_monitor_id.sql`)
- Test alert delivers to the selected channel only (not all rules)
- Channel list shows real delivery stats from `alert_logs`
- Server-side Zod validation on channel and rule creation

## [Unreleased] - Phase 6: Incident engine

### Incidents
- Dedicated incident service with enriched list/detail (monitor, assignee)
- Timeline events: DETECTED, ACKNOWLEDGED, ASSIGNED, ALERT_SENT, NOTE, RESOLVED
- Auto-open uses monitor criticality for severity; auto-resolve records system actor
- Alert dispatch appends ALERT_SENT timeline entries when channels fire
- Acknowledge auto-assigns engineer if unassigned; guards duplicate ack/resolve
- `PATCH /api/incidents/[id]` — assign engineer, severity, root cause, notes, add timeline note
- `PATCH /api/incidents/[id]/resolve` — optional root cause and resolution notes
- `GET /api/incidents/[id]` — single incident detail
- Incidents UI: resolve form, assign dropdown, add note, timeline type badges

## [Unreleased] - Phase 5: ERP / network device metadata

### Monitors
- Device fields: location, rack/cabinet, switch port, owner, criticality, vendor, model, management IP
- Future-ready SNMP fields stored (community, version, interface name) — not used by checks yet
- Migration: `supabase/migrations/003_monitor_device_fields.sql`
- Create/edit forms include **Device details** section; detail page shows device info card
- Monitor list shows category, criticality badge, and management IP when set
- Search includes IP, vendor, and model

## [Unreleased] - Phase 4: Monitoring engine

### Cron checks (`/api/cron/check-monitors`)
- Fetches active monitors, respects `check_interval`, skips maintenance windows and `maintenance_mode`
- Executes checks with `retry_count`, `timeout`, and org latency threshold (default 1000ms)
- Writes `monitor_checks`, updates status, response time, uptime %, and error message
- **UP / DOWN / DEGRADED** statuses; high latency marks checks as DEGRADED
- SSL checks detect expired certs (DOWN) and certs expiring within 14 days (DEGRADED)
- Incidents auto-open after 3 consecutive failures; auto-resolve on recovery
- Alerts suppressed during maintenance when `pause_alerts=true`
- `CRON_SECRET` required in production; cron response includes `processed` / `skipped` counts

## [Unreleased] - Phase 3: Monitor types and infrastructure fields

### Monitors
- Added **category** (ERP, Server, Switch, Router, etc.) and optional **port** columns
- Migration: `supabase/migrations/002_monitor_category_port.sql`
- Supported types: HTTP, HTTPS, API, TCP, DNS, SSL, PING (TCP-based reachability)
- Removed UDP and KEYWORD from supported types and shared enum
- PING enabled in cron scheduler; checks use `retry_count` with immediate retries
- Server-side Zod validation on monitor create/update API routes
- Create monitor form defaults to HTTP; type labels show PING as "PING (TCP)"
- Target resolution helper for host/port parsing across check types

## [Unreleased] - Phase 2: Auth and team flow

### Authentication
- Added `POST /api/auth/refresh` — rotates refresh token and renews access cookie
- Added `POST /api/auth/accept-invitation` — completes team invite and signs user in
- Password reset emails sent via SMTP (no token in API response)
- Team invitation emails with accept link
- Removed verify-email flow (redirects to login; internal accounts verified on create)
- API client auto-retries once after 401 via refresh endpoint

### Security
- Server-side Zod validation on all auth routes
- Rate limiting on auth, health, and public status routes
- JWT_SECRET required and min 32 chars in production
- Session role reloaded from database on each request (fixes stale JWT roles)
- Refresh tokens revoked on password reset

### RBAC enforcement
- VIEWER: read-only (no alert/maintenance/monitor writes)
- ENGINEER: can acknowledge and resolve incidents
- ADMIN: monitors, alerts, maintenance, team (cannot assign OWNER)
- OWNER: full access including company settings and OWNER assignment
- Team member update/delete guards: no self-disable, no self-role-change, no self-delete

### Team
- Pending invitations loaded from Supabase in `getTeam()`
- Invite validation: no OWNER invites, no duplicate users/pending invites

## [Unreleased] - Phase 1: Internal product scope

### Scope: single-company internal monitoring (not multi-tenant SaaS)
- Reframed product as internal ERP/infrastructure monitoring tool
- Added **ENGINEER** role to shared enum, Supabase schema, and team UI
- Centralized role permissions matrix in `src/lib/roles.ts` (Owner, Admin, Engineer, Viewer)
- Renamed UI copy: Organization → **Company** (settings, register, audit messages)
- Removed SaaS landing copy (free trial, multi-tenant, customer portal language)
- Landing page focuses on ERP, servers, switches, routers, firewalls
- Single-company bootstrap: registration blocked after first company exists (invite-only thereafter)
- Production default: `NEXT_PUBLIC_DEMO_MODE=false` in `.env.example` and `.env.local`
- Sidebar: Infrastructure Status link (internal, not public customer portal)
- Migration: `supabase/migrations/001_add_engineer_role.sql` for existing databases

## [Unreleased] - Supabase migration

### Database: MongoDB → Supabase PostgreSQL
- Replaced Mongoose/MongoDB with `@supabase/supabase-js` and service-role client for API routes
- Added `apps/frontend/supabase/schema.sql` (run once in Supabase SQL Editor)
- Rewrote auth, domain, monitoring, alerts, and audit services for Supabase
- New seed script: `pnpm db:seed` → `scripts/seed-supabase.mjs`
- Updated `.env.example`, `DEPLOYMENT.md`; removed `MONGODB_URI`

## [Unreleased] - Demo Polish + Monitoring Engine

### Option A — Demo gap fixes
- Dashboard charts now derive from demo store (status/type distribution, incident counts)
- Recent monitor activity shows all 12 monitors sorted by latest check
- Incident ack/resolve appends timeline events with actor name
- Completed inc-004 mock data (assigned engineer, RCA, notes)
- Settings: all 6 tabs save via unified form + `updateSettings`
- Header search navigates to `/monitors?search=`
- Mobile navigation drawer for small screens
- Loading skeleton variant on key pages; shared StatusBadge in recent tables
- Deterministic status page uptime history (no Math.random)

### Option B — Production backend + API wiring
- New `ChecksModule`: HTTP/HTTPS/API, TCP, DNS, SSL check executor
- `monitorCheck.create` on every check run; monitor status/uptime updated
- Auto-incident open on failure, auto-resolve on recovery
- In-process scheduler (default, 30s tick); optional BullMQ when `CHECKS_USE_REDIS=true`
- New monitor triggers immediate check via `ChecksScheduler.enqueueMonitorCheck`
- New `AuditModule` with `GET /audit-logs`; audit events on login, monitor/incident/settings/alert changes
- New `DashboardModule` with stats, charts, and recent feeds endpoints
- New public `GET /public/status/:slug` status page API
- Settings API extended with `enablePublicStatusPage` and `preferences` JSON
- Alerts API: `GET /alerts/deliveries` and `GET /alerts/stats`
- Frontend hooks wired for production APIs when `DEMO_MODE=false`
- Seed script seeds sample audit logs and enables public status page for demo org

## [Unreleased] - Demo Excellence Phase

### Enterprise Demo Polish
- Expanded mock data with 12 enterprise monitors, 6 incidents (timeline, RCA, assigned engineers), alert delivery history, audit logs, and status page data
- Dashboard: 24h uptime chart, status distribution chart, 7 stat cards, recent alerts/incidents/checks feeds
- New **Public Status Page** at `/status` with service health, 90-day uptime, incidents, and maintenance
- New **Audit Logs** page at `/audit-logs` with category filtering
- Alerts: delivery history, success rate stats, channel icons (Email, Slack, Teams, Telegram, WhatsApp)
- Incidents: expandable rows with timeline, root cause analysis, and resolution notes
- Team: avatars, activity status, last login, permissions matrix
- Maintenance: upcoming/active/completed tabs plus timeline view
- Settings: tabbed sections (General, Monitoring, Notifications, Security, Branding, Integrations)
- Reports: monthly/quarterly SLA, incident, and availability report cards with PDF/CSV/Excel export
- Monitors: tags, groups, uptime columns; enhanced detail page
- Shared UI: Avatar, Tabs, Skeleton components
- Sidebar: Audit Logs nav + public Status Page link

## [Unreleased] - Demo Login Seed

### Demo account
- Added `scripts/seed-demo-user.mjs` and `pnpm db:seed` for a fixed demo login:
  - **Email:** `demo@netwatch.io`
  - **Password:** `Demo1234!`
- Idempotent: re-running resets the demo password to the known value

## [Unreleased] - Demo Data Mode

### Demo Mode
- Added centralized mock data layer in `apps/frontend/src/lib/mock-data/` for monitors, incidents, alerts, reports, team, maintenance, settings, and dashboard charts
- Added `demo-store.ts` (Zustand) for in-memory state with full CRUD actions across all feature pages
- Added `DEMO_MODE` flag in `lib/demo-mode.ts` — feature pages use local state; authentication remains on real API
- Added **Demo Mode** badge in the top navigation bar
- Added toast notifications for all user actions (create, update, delete, acknowledge, resolve, invite, export, test alert)
- Dashboard stats now computed live from demo store (updates when monitors/incidents change)
- All pages fully functional for client walkthrough without monitoring engine, Redis, or BullMQ

### Interactive Demo Actions
- Add / edit / pause / delete monitors
- Acknowledge and resolve incidents
- Add alert channels and rules, test alert delivery (simulated)
- Invite team members, change roles, disable/remove members
- Schedule maintenance windows
- Update organization settings
- Export reports (PDF/CSV/Excel simulated via toast)

## [Unreleased] - Phase 1 App Pages

### Monitors (`/monitors`, `/monitors/new`, `/monitors/[id]`)
- Added backend Monitors module with full CRUD API (org-scoped)
- Monitor list page with search, status/type filters, and row actions
- Add Monitor form with React Hook Form + Zod validation
- Monitor detail page with stats, response chart, checks, incidents, and alert history

### Incidents (`/incidents`)
- Added backend Incidents module with list, acknowledge, and resolve endpoints
- Incident table with status filters and action buttons

### Alerts (`/alerts`)
- Added backend Alerts module for channels, rules, test alerts, and logs
- Alert channels and rules management UI with create forms and test button

### Reports (`/reports`)
- Added backend Reports module with date-range summary API
- Uptime cards, response/incident charts, monitor uptime table
- Export buttons (PDF/CSV/Excel) stubbed for worker phase

### Team (`/team`)
- Extended Organizations API with member role update and removal
- Team page with invite form, role management, disable/remove actions

### Maintenance (`/maintenance`)
- Added backend Maintenance module for scheduling windows
- Create maintenance form with monitor selection and pause-alerts toggle

### Settings (`/settings`)
- Added backend Settings module for organization thresholds and notifications
- Organization profile, threshold settings, theme toggle, account info

### Navigation
- Removed SOON labels from sidebar
- All routes functional: `/dashboard`, `/monitors`, `/incidents`, `/alerts`, `/reports`, `/team`, `/maintenance`, `/settings`
- Active route highlighting for nested paths (e.g. `/monitors/new`)

### Database
- Added Prisma models: Monitor, MonitorCheck, Incident, AlertChannelConfig, AlertRule, AlertLog, MaintenanceWindow, OrganizationSettings
