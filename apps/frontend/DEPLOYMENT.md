# NetWatch — Production Deployment Guide

NetWatch is an **internal infrastructure monitoring platform** for a single company. It monitors ERP systems, servers, network devices, APIs, databases, SSL certificates, and critical services.

**Production stack:** Next.js 15 on Vercel + Supabase PostgreSQL + Vercel Cron.

**Not used in production:** `apps/backend` (legacy NestJS), Docker Compose, Redis, BullMQ.

---

## Prerequisites

| Requirement | Version |
|-------------|---------|
| Node.js | 20+ |
| pnpm | 9+ |
| Supabase account | Free tier or higher |
| Vercel account | Hobby or Pro (cron on Pro recommended for 1-min schedule) |
| SMTP provider | Optional but recommended (alerts + invite/reset emails) |

---

## 1. Supabase database

### 1.1 Create project

1. Go to [supabase.com](https://supabase.com) → **New project**.
2. Choose a region close to your users and Vercel deployment region.
3. Save the database password securely.

### 1.2 Run schema

In **SQL Editor**, run in order:

1. `apps/frontend/supabase/schema.sql` — full base schema
2. Migrations (if not already included in a fresh `schema.sql`):

| File | Purpose |
|------|---------|
| `001_add_engineer_role.sql` | ENGINEER role support |
| `002_monitor_category_port.sql` | Monitor category + port |
| `003_monitor_device_fields.sql` | Device metadata fields |
| `004_alert_logs_monitor_id.sql` | `monitor_id` on alert logs |

> For a **brand-new** database, run `schema.sql` first, then apply any migrations whose columns are missing.

### 1.3 API keys

From **Project Settings → API**:

- **Project URL** → `NEXT_PUBLIC_SUPABASE_URL`
- **service_role key** → `SUPABASE_SERVICE_ROLE_KEY` (server only — never expose to the browser)

The anon key is optional; the app uses the service role on API routes.

---

## 2. Environment variables

Set these in **Vercel → Project → Settings → Environment Variables** (Production + Preview as needed).

### Required (production)

| Variable | Description |
|----------|-------------|
| `NEXT_PUBLIC_SUPABASE_URL` | Supabase project URL |
| `SUPABASE_SERVICE_ROLE_KEY` | Service role key (secret) |
| `JWT_SECRET` | Random string, **min 32 characters** |
| `CRON_SECRET` | Random string for cron auth (`Authorization: Bearer …`) |
| `NEXT_PUBLIC_DEMO_MODE` | Must be `false` for real data |
| `APP_URL` | Public app URL, e.g. `https://netwatch.yourcompany.com` |

### Recommended

| Variable | Description |
|----------|-------------|
| `NEXT_PUBLIC_API_URL` | `/api` (default) |
| `JWT_EXPIRES_IN` | Access token TTL override, e.g. `15m` |
| `SMTP_HOST` | SMTP server hostname |
| `SMTP_PORT` | Usually `587` |
| `SMTP_USER` | SMTP username |
| `SMTP_PASSWORD` | SMTP password |
| `SMTP_FROM` | From address, e.g. `alerts@yourcompany.com` |
| `SMTP_SECURE` | `true` for port 465 |

### Example (production)

```env
NEXT_PUBLIC_API_URL=/api
NEXT_PUBLIC_SUPABASE_URL=https://xxxxxxxx.supabase.co
SUPABASE_SERVICE_ROLE_KEY=eyJhbGciOi...
JWT_SECRET=replace-with-openssl-rand-hex-32-or-longer
JWT_EXPIRES_IN=15m
CRON_SECRET=replace-with-another-strong-secret
NEXT_PUBLIC_DEMO_MODE=false
APP_URL=https://netwatch.yourcompany.com

SMTP_HOST=smtp.yourprovider.com
SMTP_PORT=587
SMTP_USER=alerts@yourcompany.com
SMTP_PASSWORD=...
SMTP_FROM=NetWatch <alerts@yourcompany.com>
```

Generate secrets:

```bash
# Linux/macOS/Git Bash
openssl rand -hex 32
```

---

## 3. Vercel deployment

### 3.1 Connect repository

1. Import the Git repository in Vercel.
2. Set **Root Directory** to `apps/frontend`.
3. Framework preset: **Next.js**.
4. Build command: `pnpm build` (from root, Vercel runs install at monorepo root if configured)  
   - Or set install: `cd ../.. && pnpm install` and build: `pnpm --filter @netwatch/frontend build` depending on your Vercel monorepo settings.

**Monorepo tip:** In Vercel project settings:

- **Root Directory:** `apps/frontend`
- Enable **Include files outside the root directory** if builds fail to resolve `packages/shared`.

### 3.2 Add environment variables

Add all variables from [section 2](#2-environment-variables) for the **Production** environment.

### 3.3 Cron job

`apps/frontend/vercel.json` registers:

```json
{
  "crons": [
    {
      "path": "/api/cron/check-monitors",
      "schedule": "* * * * *"
    }
  ]
}
```

Vercel automatically sends `Authorization: Bearer <CRON_SECRET>` when `CRON_SECRET` is set in the project.

> **Note:** 1-minute cron requires a Vercel plan that supports frequent crons. On Hobby, consider `*/5 * * * *` if limits apply.

### 3.4 Deploy

Push to your production branch or click **Deploy** in Vercel.

---

## 4. First-time bootstrap

### Option A — Register first owner (recommended for production)

1. Ensure **no** organization exists in Supabase (`organizations` table empty).
2. Visit `https://your-app/register`.
3. Create the owner account — this creates the company and settings row.
4. Invite team members from **Team** (Admin/Owner only).

### Option B — Seed script (dev/staging only)

```bash
# From repo root, with .env pointing at your Supabase project
pnpm db:seed
```

Default login: `demo@netwatch.io` / `Demo1234!`

Do **not** use the seed script in production unless you change credentials immediately.

---

## 5. Post-deploy verification

Run through this checklist after the first production deploy.

### 5.1 Health

```bash
curl https://your-app.vercel.app/api/health
```

Expect `{ "success": true, ... }`.

### 5.2 Cron

```bash
curl -H "Authorization: Bearer YOUR_CRON_SECRET" \
  https://your-app.vercel.app/api/cron/check-monitors
```

Expect JSON with `processed`, `skipped`, and `retention` counts.

### 5.3 Application smoke test

| Step | Action | Expected |
|------|--------|----------|
| 1 | Log in as Owner | Dashboard loads with real stats |
| 2 | Create a monitor (HTTP) | Monitor appears; cron writes `monitor_checks` within 1–2 min |
| 3 | Open **Alerts** | Channels/rules load from API |
| 4 | Schedule maintenance | Window appears; active window skips checks |
| 5 | Open **Settings** | Owner can save latency threshold |
| 6 | Open **Reports** | CSV export downloads |
| 7 | Visit `/status?slug=YOUR_ORG_SLUG` | Public status page (if enabled in settings) |

### 5.4 Email (if SMTP configured)

1. **Settings → Notifications** — set default notification email.
2. **Alerts** — create Email channel + rule, click **Test**.
3. **Team** — invite a user; confirm invite email arrives with correct `APP_URL` link.

---

## 6. Security configuration

Configure after deploy via **Settings** (Owner only):

| Setting | Effect |
|---------|--------|
| Session timeout | Access + refresh token lifetime on login |
| IP allowlist | Comma-separated IPs; blocks API access from other IPs (logged in audit) |
| Public status page | Toggle `/api/public/status/[slug]` |
| Require MFA | Policy stored; enrollment enforcement planned |

**Built-in protections:**

- Security headers (middleware + `next.config.ts`)
- API rate limiting (middleware + per-route guards)
- `CRON_SECRET` required in production
- `JWT_SECRET` min 32 chars in production
- Audit logs for security events (Owner/Admin only)
- RBAC on all write routes

---

## 7. Roles reference

| Role | Permissions |
|------|-------------|
| **Owner** | Everything including company settings |
| **Admin** | Monitors, alerts, maintenance, team, audit logs |
| **Engineer** | View monitors/reports; acknowledge & resolve incidents |
| **Viewer** | Read-only |

---

## 8. Operations

### 8.1 Adding monitors

1. **Monitors → Add Monitor**
2. Defaults for interval/timeout come from **Settings → Monitoring**
3. Cron respects `check_interval`, maintenance windows, and `maintenance_mode`

### 8.2 Data retention

**Settings → Monitoring → Data retention (days)** controls how long `monitor_checks` are kept. Purge runs on each cron execution.

### 8.3 Backups

Use Supabase **Database → Backups** (plan-dependent). Export critical config:

- `organization_settings`
- `monitors`, `alert_channels`, `alert_rules`

### 8.4 Logs

- **Vercel → Logs** — API errors, cron invocations
- **Audit Logs** in app — user/security actions
- **Supabase → Logs** — database errors

---

## 9. Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Dashboard shows mock/empty data | `NEXT_PUBLIC_DEMO_MODE=true` | Set to `false`, redeploy |
| Cron returns 500 | `CRON_SECRET` missing | Add to Vercel env |
| Cron returns 401 | Wrong secret | Match `CRON_SECRET` in Vercel |
| Login works locally, not on Vercel | Wrong Supabase URL/key | Verify env vars on Production |
| No emails | SMTP not set | Configure `SMTP_*`; without SMTP, emails log to console only |
| Invite links go to localhost | `APP_URL` wrong | Set to production URL |
| 403 on all API calls | IP allowlist | Clear allowlist in Settings or add your IP |
| Monitors stay UNKNOWN | Cron not running | Check Vercel cron logs; verify plan supports schedule |
| JWT errors on deploy | Weak/missing `JWT_SECRET` | Use 32+ char secret |

### Local cron test

```bash
pnpm dev
# In another terminal:
curl -H "Authorization: Bearer your-cron-secret" http://localhost:3001/api/cron/check-monitors
```

---

## 10. Local development

```bash
pnpm install
cp apps/frontend/.env.example apps/frontend/.env.local
# Fill in Supabase keys + secrets
pnpm dev
```

App: http://localhost:3001

**Tests and typecheck** — run from the **repo root** (recommended):

```powershell
pnpm test
pnpm typecheck
```

If you are already inside `apps/frontend`, run the commands directly — do **not** `cd apps/frontend` again:

```powershell
pnpm test
pnpm typecheck
```

To enter the frontend folder once from repo root:

```powershell
cd apps/frontend
pnpm test
pnpm typecheck
```

On Windows PowerShell, use `;` to chain on one line (e.g. `cd apps/frontend; pnpm test`). Only `cd` when you are still at the repo root.

---

## 11. Architecture (production)

```
Browser
   │
   ▼
Vercel (Next.js 15)
   ├── /app/(app)/*     Dashboard UI
   ├── /api/*           REST API (session JWT cookies)
   └── /api/cron/*      Scheduled monitor checks
           │
           ▼
   Supabase PostgreSQL
   ├── monitors, monitor_checks, incidents
   ├── alert_channels, alert_rules, alert_logs
   ├── maintenance_windows, organization_settings
   └── users, refresh_tokens, audit_logs
```

---

## 12. Upgrade / migrations

When pulling a new version:

1. Review `CHANGELOG.md`
2. Run any new SQL files in `apps/frontend/supabase/migrations/`
3. Deploy to Vercel (or let CI deploy)
4. Verify cron + one monitor check

---

## 13. Deprecated components

| Path | Status |
|------|--------|
| `apps/backend` | Legacy NestJS — do not deploy |
| `docker-compose.yml` | Dev/legacy — not required for Vercel production |
| `NEXT_PUBLIC_DEMO_MODE=true` | UI preview only — not for production |

---

## Support checklist (quick reference)

```
[ ] Supabase schema + migrations applied
[ ] All required env vars set on Vercel Production
[ ] NEXT_PUBLIC_DEMO_MODE=false
[ ] CRON_SECRET set; cron endpoint returns 200
[ ] Owner account created
[ ] At least one monitor checking successfully
[ ] SMTP tested (if using email alerts)
[ ] APP_URL correct for invite/reset links
[ ] Public status page tested (if enabled)
```
