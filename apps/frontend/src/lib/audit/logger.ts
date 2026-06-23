import { getSupabaseAdmin } from '@/lib/db/supabase';
import { getClientIp } from '@/lib/api/rate-limit';

export type AuditCategory =
  | 'USER'
  | 'MONITOR'
  | 'ALERT'
  | 'SECURITY'
  | 'SETTINGS'
  | 'INCIDENT'
  | 'MAINTENANCE';

export interface AuditInput {
  organizationId: string;
  category: AuditCategory;
  action: string;
  actor: string;
  actorUserId?: string;
  target?: string;
  ipAddress?: string;
}

export async function logAudit(input: AuditInput) {
  const db = getSupabaseAdmin();
  await db.from('audit_logs').insert({
    organization_id: input.organizationId,
    category: input.category,
    action: input.action,
    actor: input.actor,
    actor_user_id: input.actorUserId ?? null,
    target: input.target ?? null,
    ip_address: input.ipAddress ?? null,
  });
}

export async function logAuditFromRequest(
  req: Request,
  input: Omit<AuditInput, 'ipAddress'>,
) {
  return logAudit({ ...input, ipAddress: getClientIp(req) });
}

export async function logFailedLogin(req: Request, email: string) {
  const db = getSupabaseAdmin();
  const { data: user } = await db
    .from('users')
    .select('id, organization_id, email')
    .eq('email', email.toLowerCase())
    .is('deleted_at', null)
    .maybeSingle();
  if (!user) return;

  await logAuditFromRequest(req, {
    organizationId: user.organization_id,
    category: 'SECURITY',
    action: 'Failed login attempt',
    actor: email,
    actorUserId: user.id,
    target: 'auth/login',
  });
}

export async function logIpAllowlistBlock(
  req: Request,
  organizationId: string,
  actor: string,
  actorUserId: string,
) {
  await logAuditFromRequest(req, {
    organizationId,
    category: 'SECURITY',
    action: 'Blocked request — IP not on allowlist',
    actor,
    actorUserId,
    target: 'ip-allowlist',
  });
}
