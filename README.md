# NetWatch

Internal infrastructure monitoring for a single company — ERP systems, servers, network devices, APIs, SSL, and critical services.

## Stack (production)

| Layer | Technology |
|-------|------------|
| App | Next.js 15 (App Router) on Vercel |
| Database | Supabase PostgreSQL |
| Monitoring | Vercel Cron → `/api/cron/check-monitors` |
| Auth | Custom JWT + httpOnly cookies |

The legacy `apps/backend` NestJS service is **not used** in production.

## Quick start (local)

```bash
pnpm install
cp apps/frontend/.env.example apps/frontend/.env.local
# Add Supabase URL + SUPABASE_SERVICE_ROLE_KEY + JWT_SECRET + CRON_SECRET
pnpm dev
```

Open http://localhost:3001

Optional seed data:

```bash
pnpm db:seed
# Login: demo@netwatch.io / Demo1234!
```

## Production deployment

See **[apps/frontend/DEPLOYMENT.md](apps/frontend/DEPLOYMENT.md)** for the full guide:

- Supabase setup and migrations
- Vercel configuration and cron
- Environment variables
- Post-deploy verification checklist
- Security and operations

## Project structure

```
apps/frontend/          # Next.js app (deploy this to Vercel)
  src/app/api/          # API routes
  src/lib/monitoring/   # Check executor + cron
  src/lib/services/     # Business logic
  supabase/             # schema.sql + migrations
packages/shared/        # Shared types and enums
apps/backend/           # Legacy — not deployed
```

## Key environment variables

| Variable | Required |
|----------|----------|
| `NEXT_PUBLIC_SUPABASE_URL` | Yes |
| `SUPABASE_SERVICE_ROLE_KEY` | Yes |
| `JWT_SECRET` (32+ chars) | Yes |
| `CRON_SECRET` | Yes (production) |
| `NEXT_PUBLIC_DEMO_MODE=false` | Yes (production) |
| `APP_URL` | Yes (production) |
| `SMTP_*` | Optional (email alerts) |

## Roles

| Role | Access |
|------|--------|
| Owner | Full access + company settings |
| Admin | Monitors, alerts, maintenance, team, audit logs |
| Engineer | Incidents, view monitors & reports |
| Viewer | Read-only |

## License

Proprietary — All rights reserved.
