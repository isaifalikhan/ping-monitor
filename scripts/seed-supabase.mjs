/**
 * Seeds demo user into Supabase PostgreSQL.
 * Prerequisites: run apps/frontend/supabase/schema.sql in Supabase SQL Editor first.
 *
 * Run: pnpm db:seed
 * Login: demo@netwatch.io / Demo1234!
 */
import { createClient } from '@supabase/supabase-js';
import bcrypt from 'bcryptjs';
import { readFileSync } from 'fs';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));

const DEMO = {
  email: 'demo@netwatch.io',
  password: 'Demo1234!',
  firstName: 'Demo',
  lastName: 'User',
  organizationName: 'Company Internal',
  orgSlug: 'company-internal',
};

function loadEnv(key) {
  if (process.env[key]) return process.env[key];
  const envPath = resolve(__dirname, '../apps/frontend/.env.local');
  try {
    const content = readFileSync(envPath, 'utf8');
    for (const line of content.split('\n')) {
      const trimmed = line.trim();
      if (!trimmed || trimmed.startsWith('#')) continue;
      const [k, ...rest] = trimmed.split('=');
      if (k.trim() === key) return rest.join('=').trim();
    }
  } catch {
    /* ignore */
  }
  return undefined;
}

const url = loadEnv('NEXT_PUBLIC_SUPABASE_URL') || loadEnv('SUPABASE_URL');
const serviceKey =
  loadEnv('SUPABASE_SERVICE_ROLE_KEY') || loadEnv('SUPABASE_SECRET_KEY');
if (!url || !serviceKey) {
  throw new Error(
    'Set NEXT_PUBLIC_SUPABASE_URL + SUPABASE_SERVICE_ROLE_KEY (or SUPABASE_SECRET_KEY) in apps/frontend/.env.local',
  );
}

const db = createClient(url, serviceKey, {
  auth: { persistSession: false, autoRefreshToken: false },
});

let { data: org } = await db
  .from('organizations')
  .select('id, name, slug')
  .eq('slug', DEMO.orgSlug)
  .is('deleted_at', null)
  .maybeSingle();

if (!org) {
  const { data: created, error } = await db
    .from('organizations')
    .insert({ name: DEMO.organizationName, slug: DEMO.orgSlug })
    .select('id, name, slug')
    .single();
  if (error) throw new Error(error.message);
  org = created;
  console.log('Created demo organization');
}

const passwordHash = await bcrypt.hash(DEMO.password, 12);
let { data: user } = await db
  .from('users')
  .select('id')
  .eq('email', DEMO.email.toLowerCase())
  .eq('organization_id', org.id)
  .maybeSingle();

if (user) {
  const { error } = await db
    .from('users')
    .update({
      password_hash: passwordHash,
      role: 'OWNER',
      email_verified: true,
      is_active: true,
    })
    .eq('id', user.id);
  if (error) throw new Error(error.message);
  console.log('Updated demo user password');
} else {
  const { data: created, error } = await db
    .from('users')
    .insert({
      email: DEMO.email.toLowerCase(),
      password_hash: passwordHash,
      first_name: DEMO.firstName,
      last_name: DEMO.lastName,
      role: 'OWNER',
      email_verified: true,
      is_active: true,
      organization_id: org.id,
    })
    .select('id')
    .single();
  if (error) throw new Error(error.message);
  user = created;
  console.log('Created demo user');
}

await db.from('organization_settings').upsert({
  organization_id: org.id,
  latency_threshold_ms: 500,
  notification_email: DEMO.email,
  timezone: 'UTC',
  enable_public_status_page: true,
  preferences: { statusPageSlug: DEMO.orgSlug },
});

await db.from('status_pages').upsert({
  organization_id: org.id,
  slug: DEMO.orgSlug,
  enabled: true,
});

const { count: auditCount } = await db
  .from('audit_logs')
  .select('*', { count: 'exact', head: true })
  .eq('organization_id', org.id);

if (!auditCount) {
  const samples = [
    { category: 'SECURITY', action: 'User logged in', actor: DEMO.email, minsAgo: 5 },
    { category: 'SETTINGS', action: 'Updated organization settings', actor: DEMO.email, minsAgo: 120 },
    { category: 'MONITOR', action: 'Created monitor "API Health Check"', actor: DEMO.email, minsAgo: 360 },
  ];
  for (const s of samples) {
    await db.from('audit_logs').insert({
      organization_id: org.id,
      category: s.category,
      action: s.action,
      actor: s.actor,
      created_at: new Date(Date.now() - s.minsAgo * 60_000).toISOString(),
    });
  }
  console.log(`Seeded ${samples.length} audit logs`);
}

console.log('\n══════════════════════════════════════');
console.log('  NetWatch demo login (Supabase)');
console.log('══════════════════════════════════════');
console.log('  Email:    demo@netwatch.io');
console.log('  Password: Demo1234!');
console.log('══════════════════════════════════════\n');
