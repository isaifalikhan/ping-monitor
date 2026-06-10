# NetWatch

Enterprise-grade Network Monitoring and Infrastructure Health Platform.

## Tech Stack

- **Frontend:** Next.js 15, React, TypeScript, Tailwind CSS, Shadcn/UI, TanStack Query, Zustand
- **Backend:** NestJS, Prisma, PostgreSQL, Redis, BullMQ
- **Infrastructure:** Docker, Docker Compose, Nginx

## Project Structure

```
apps/
  frontend/     # Next.js 15 App Router
  backend/      # NestJS API
packages/
  shared/       # Shared types, enums, validators
  config/       # Environment validation
infrastructure/
  docker/       # Production Dockerfiles
  nginx/        # Nginx reverse proxy config
```

## Getting Started

### Prerequisites

- Node.js 20+
- pnpm 9+
- Docker & Docker Compose

### Setup

1. Copy environment variables:
   ```bash
   cp .env.example .env
   ```

2. Start infrastructure:
   ```bash
   docker compose up -d
   ```

3. Install dependencies:
   ```bash
   pnpm install
   ```

4. Run database migrations:
   ```bash
   pnpm db:generate
   pnpm db:migrate
   ```

5. Start development servers:
   ```bash
   pnpm dev
   ```

- Frontend: http://localhost:3000
- Backend API: http://localhost:4000/api/v1
- API Docs: http://localhost:4000/docs
- MailHog: http://localhost:8025

## Phase 1 Progress

- [x] **Module 1: Authentication & Organizations**
  - Register, Login, Logout, Refresh Token
  - Forgot/Reset Password, Email Verification
  - Team Invitations
  - Multi-tenant organization scoping
  - Role-based access control (OWNER, ADMIN, VIEWER)
- [ ] Module 2: Dashboard (full)
- [ ] Module 3: Monitor CRUD
- [ ] Module 4: Ping Monitoring
- [ ] Module 5: Email Alerts

## License

Proprietary - All rights reserved.
