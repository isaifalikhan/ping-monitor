# Changelog

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
