import { cookies, headers } from 'next/headers';
import { COOKIE_NAMES } from '@netwatch/shared';
import { verifyAccessToken } from './jwt';
import type { JwtPayload } from '@netwatch/shared';
import { UserRole, hasMinimumRole } from '@netwatch/shared';
import { getSupabaseAdmin } from '@/lib/db/supabase';
import { getClientIp, isIpAllowed, mergePreferences } from '@/lib/settings/enforce';

async function hydrateSession(session: JwtPayload): Promise<JwtPayload> {
  const db = getSupabaseAdmin();
  const { data: user } = await db
    .from('users')
    .select('role, is_active, organization_id, email')
    .eq('id', session.sub)
    .is('deleted_at', null)
    .maybeSingle();
  if (!user || !user.is_active) throw new AuthError('Unauthorized', 401);
  return {
    ...session,
    email: user.email,
    organizationId: user.organization_id,
    role: user.role as UserRole,
  };
}

async function enforceIpAllowlist(organizationId: string, actor: string, actorUserId: string) {
  const db = getSupabaseAdmin();
  const { data: settings } = await db
    .from('organization_settings')
    .select('preferences')
    .eq('organization_id', organizationId)
    .maybeSingle();
  const prefs = mergePreferences(settings?.preferences as object);
  const headerStore = await headers();
  const clientIp = getClientIp(headerStore);
  if (!isIpAllowed(clientIp, prefs.ipAllowlist)) {
    const { logIpAllowlistBlock } = await import('@/lib/audit/logger');
    const reqLike = { headers: { get: (n: string) => headerStore.get(n) } } as Request;
    await logIpAllowlistBlock(reqLike, organizationId, actor, actorUserId);
    throw new AuthError('Access denied from this IP address', 403);
  }
}

export async function getSession(): Promise<JwtPayload | null> {
  const cookieStore = await cookies();
  const token = cookieStore.get(COOKIE_NAMES.ACCESS_TOKEN)?.value;
  if (!token) return null;
  try {
    const payload = verifyAccessToken(token);
    const session = await hydrateSession(payload);
    await enforceIpAllowlist(session.organizationId, session.email, session.sub);
    return session;
  } catch (err) {
    if (err instanceof AuthError) throw err;
    return null;
  }
}

export async function requireSession(): Promise<JwtPayload> {
  const session = await getSession();
  if (!session) throw new AuthError('Unauthorized', 401);
  return session;
}

export async function requireRole(minRole: UserRole): Promise<JwtPayload> {
  const session = await requireSession();
  if (!hasMinimumRole(session.role, minRole)) {
    throw new AuthError('Forbidden', 403);
  }
  return session;
}

export class AuthError extends Error {
  constructor(
    message: string,
    public status: number,
  ) {
    super(message);
    this.name = 'AuthError';
  }
}
