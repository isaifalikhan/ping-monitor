# Changelog

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
