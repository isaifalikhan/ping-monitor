/**
 * Seeds a fixed demo account for client walkthroughs.
 * Safe to run multiple times (idempotent).
 *
 * Login:
 *   Email:    demo@netwatch.io
 *   Password: Demo1234!
 */
import { createRequire } from 'module';
import { readFileSync } from 'fs';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';
import { randomUUID } from 'crypto';
import postgres from 'postgres';

const __dirname = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const bcrypt = require('../apps/backend/node_modules/bcryptjs');

const DEMO = {
  email: 'demo@netwatch.io',
  password: 'Demo1234!',
  firstName: 'Demo',
  lastName: 'User',
  organizationName: 'NetWatch Demo',
  orgSlug: 'netwatch-demo',
};

const BCRYPT_ROUNDS = 12;

function loadDatabaseUrl() {
  if (process.env.DATABASE_URL) return process.env.DATABASE_URL;

  const envPath = resolve(__dirname, '../apps/backend/.env');
  const content = readFileSync(envPath, 'utf8');
  for (const line of content.split('\n')) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;
    const eq = trimmed.indexOf('=');
    if (eq === -1) continue;
    const key = trimmed.slice(0, eq).trim();
    const value = trimmed.slice(eq + 1).trim();
    if (key === 'DATABASE_URL') return value;
  }

  throw new Error('DATABASE_URL not found in environment or apps/backend/.env');
}

const SAMPLE_AUDIT_LOGS = [
  { category: 'SECURITY', action: 'User logged in', actor: 'demo@netwatch.io', target: null, minsAgo: 5 },
  { category: 'SETTINGS', action: 'Updated organization settings', actor: 'demo@netwatch.io', target: 'Organization settings', minsAgo: 120 },
  { category: 'MONITOR', action: 'Created monitor "API Health Check"', actor: 'demo@netwatch.io', target: 'monitor', minsAgo: 360 },
  { category: 'INCIDENT', action: 'Acknowledged incident', actor: 'demo@netwatch.io', target: 'API Health Check', minsAgo: 720 },
  { category: 'ALERT', action: 'Created alert channel "Email Notifications"', actor: 'demo@netwatch.io', target: 'channel', minsAgo: 1440 },
  { category: 'USER', action: 'Invited team member viewer@netwatch.io', actor: 'demo@netwatch.io', target: 'VIEWER role', minsAgo: 2880 },
];

const sql = postgres(loadDatabaseUrl());

async function seedAuditLogs(orgId) {
  const [{ count }] = await sql`
    SELECT COUNT(*)::int AS count FROM audit_logs WHERE organization_id = ${orgId}
  `;
  if (count > 0) return;

  for (const entry of SAMPLE_AUDIT_LOGS) {
    await sql`
      INSERT INTO audit_logs (
        id, organization_id, category, action, actor, target, created_at
      ) VALUES (
        ${randomUUID()}, ${orgId}, ${entry.category}, ${entry.action},
        ${entry.actor}, ${entry.target},
        NOW() - (${entry.minsAgo} * INTERVAL '1 minute')
      )
    `;
  }
  console.log(`Seeded ${SAMPLE_AUDIT_LOGS.length} sample audit logs.`);
}

try {
  const passwordHash = await bcrypt.hash(DEMO.password, BCRYPT_ROUNDS);
  const email = DEMO.email.toLowerCase();
  let orgId;

  const existingUsers = await sql`
    SELECT u.id, u.organization_id, o.slug
    FROM users u
    JOIN organizations o ON o.id = u.organization_id
    WHERE u.email = ${email} AND u.deleted_at IS NULL
    LIMIT 1
  `;

  if (existingUsers.length > 0) {
    const user = existingUsers[0];
    orgId = user.organization_id;
    await sql`
      UPDATE users
      SET password_hash = ${passwordHash},
          email_verified = true,
          is_active = true,
          first_name = ${DEMO.firstName},
          last_name = ${DEMO.lastName},
          role = 'OWNER',
          updated_at = NOW()
      WHERE id = ${user.id}
    `;
    console.log('Demo user already exists — password reset to known demo value.\n');
  } else {
    orgId = randomUUID();
    const userId = randomUUID();

    await sql.begin(async (tx) => {
      await tx`
        INSERT INTO organizations (id, name, slug, created_at, updated_at)
        VALUES (${orgId}, ${DEMO.organizationName}, ${DEMO.orgSlug}, NOW(), NOW())
      `;

      await tx`
        INSERT INTO users (
          id, email, password_hash, first_name, last_name,
          role, email_verified, is_active, organization_id, created_at, updated_at
        ) VALUES (
          ${userId}, ${email}, ${passwordHash}, ${DEMO.firstName}, ${DEMO.lastName},
          'OWNER', true, true, ${orgId}, NOW(), NOW()
        )
      `;
    });

    console.log('Demo user created.\n');
  }

  await sql`
    INSERT INTO organization_settings (
      organization_id, latency_threshold_ms, notification_email, timezone,
      enable_public_status_page, preferences, created_at, updated_at
    ) VALUES (
      ${orgId}, 500, ${email}, 'UTC', true,
      ${JSON.stringify({ statusPageSlug: DEMO.orgSlug, defaultCheckInterval: 60, defaultTimeout: 30 })},
      NOW(), NOW()
    )
    ON CONFLICT (organization_id) DO UPDATE SET
      enable_public_status_page = true,
      preferences = COALESCE(organization_settings.preferences, '{}'::jsonb) || ${JSON.stringify({ statusPageSlug: DEMO.orgSlug })}::jsonb,
      updated_at = NOW()
  `;

  await seedAuditLogs(orgId);

  console.log('══════════════════════════════════════');
  console.log('  NetWatch demo login');
  console.log('══════════════════════════════════════');
  console.log(`  URL:      http://localhost:3001/login`);
  console.log(`  Email:    ${DEMO.email}`);
  console.log(`  Password: ${DEMO.password}`);
  console.log('══════════════════════════════════════');
} catch (err) {
  console.error('Seed failed:', err.message);
  console.error('\nMake sure the database is running:');
  console.error('  node scripts/start-dev-db.mjs');
  process.exit(1);
} finally {
  await sql.end();
}
