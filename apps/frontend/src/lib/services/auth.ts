import { randomBytes } from 'crypto';
import bcrypt from 'bcryptjs';
import { BCRYPT_ROUNDS, slugify, UserRole, type AuthUser, type AuthTokens } from '@netwatch/shared';
import { getSupabaseAdmin } from '@/lib/db/supabase';
import { mapUserRow } from '@/lib/db/mappers';
import { signAccessToken } from '@/lib/auth/jwt';
import { logAudit } from '@/lib/audit/logger';
import { sendPasswordResetEmail, sendTeamInvitationEmail } from '@/lib/email/mailer';
import { canAssignRole, INVITABLE_ROLES } from '@/lib/roles';

type UserRow = {
  id: string;
  email: string;
  first_name: string;
  last_name: string;
  role: string;
  email_verified: boolean;
  organization_id: string;
};

function secureToken() {
  return randomBytes(32).toString('hex');
}

export async function generateTokens(user: UserRow): Promise<AuthTokens> {
  const payload = {
    sub: user.id,
    email: user.email,
    organizationId: user.organization_id,
    role: user.role as AuthUser['role'],
    type: 'access' as const,
  };
  const db = getSupabaseAdmin();
  const { loadOrganizationSettings } = await import('@/lib/services/settings');
  const { accessTokenExpiresIn, refreshTokenExpiresAt } = await import('@/lib/settings/enforce');
  const orgSettings = await loadOrganizationSettings(user.organization_id);
  const accessExpiresIn = accessTokenExpiresIn(orgSettings.sessionTimeoutMinutes);
  const refreshExpiresAt = refreshTokenExpiresAt(orgSettings.sessionTimeoutMinutes);
  const accessToken = signAccessToken(payload, accessExpiresIn);
  const refreshToken = secureToken();
  const { error } = await db.from('refresh_tokens').insert({
    token: refreshToken,
    user_id: user.id,
    expires_at: refreshExpiresAt.toISOString(),
  });
  if (error) throw new Error(error.message);
  const expiresMinutes = Math.min(60, orgSettings.sessionTimeoutMinutes);
  return { accessToken, refreshToken, expiresIn: expiresMinutes * 60 };
}

export async function registerUser(data: {
  email: string;
  password: string;
  firstName: string;
  lastName: string;
  organizationName: string;
}) {
  const db = getSupabaseAdmin();

  const { count: companyCount } = await db
    .from('organizations')
    .select('*', { count: 'exact', head: true })
    .is('deleted_at', null);
  if (companyCount && companyCount > 0) {
    throw new Error(
      'A company account already exists. Ask an administrator to send you a team invitation.',
    );
  }

  const slug = slugify(data.organizationName);

  const { data: existingOrg } = await db
    .from('organizations')
    .select('id')
    .eq('slug', slug)
    .is('deleted_at', null)
    .maybeSingle();
  if (existingOrg) throw new Error('Company name is already taken');

  const passwordHash = await bcrypt.hash(data.password, BCRYPT_ROUNDS);

  const { data: org, error: orgErr } = await db
    .from('organizations')
    .insert({ name: data.organizationName, slug })
    .select('id, name, slug')
    .single();
  if (orgErr || !org) throw new Error(orgErr?.message ?? 'Failed to create company');

  const { data: user, error: userErr } = await db
    .from('users')
    .insert({
      email: data.email.toLowerCase(),
      password_hash: passwordHash,
      first_name: data.firstName,
      last_name: data.lastName,
      role: 'OWNER',
      organization_id: org.id,
      email_verified: true,
    })
    .select('id, email, first_name, last_name, role, email_verified, organization_id')
    .single();
  if (userErr || !user) throw new Error(userErr?.message ?? 'Failed to create user');

  await db.from('organization_settings').insert({ organization_id: org.id });
  await db.from('status_pages').insert({ organization_id: org.id, slug, enabled: true });

  const tokens = await generateTokens(user);
  return {
    user: mapUserRow(user),
    organization: { id: org.id, name: org.name, slug: org.slug },
    tokens,
  };
}

export async function loginUser(email: string, password: string, meta?: { ipAddress?: string }) {
  const db = getSupabaseAdmin();
  const { data: user, error } = await db
    .from('users')
    .select('id, email, password_hash, first_name, last_name, role, email_verified, organization_id, is_active')
    .eq('email', email.toLowerCase())
    .is('deleted_at', null)
    .eq('is_active', true)
    .maybeSingle();
  if (error || !user) throw new Error('Invalid credentials');

  const valid = await bcrypt.compare(password, user.password_hash);
  if (!valid) throw new Error('Invalid credentials');

  await db.from('users').update({ last_login_at: new Date().toISOString() }).eq('id', user.id);

  const { data: org } = await db
    .from('organizations')
    .select('id, name, slug')
    .eq('id', user.organization_id)
    .single();
  if (!org) throw new Error('Organization not found');

  const tokens = await generateTokens(user);
  await logAudit({
    organizationId: user.organization_id,
    category: 'SECURITY',
    action: 'User logged in',
    actor: user.email,
    actorUserId: user.id,
    ipAddress: meta?.ipAddress,
  });

  return {
    user: mapUserRow(user),
    organization: org,
    tokens,
  };
}

export async function getMe(userId: string) {
  const db = getSupabaseAdmin();
  const { data: user } = await db
    .from('users')
    .select('id, email, first_name, last_name, role, email_verified, organization_id')
    .eq('id', userId)
    .is('deleted_at', null)
    .single();
  if (!user) throw new Error('User not found');

  const { data: org } = await db
    .from('organizations')
    .select('id, name, slug')
    .eq('id', user.organization_id)
    .single();
  if (!org) throw new Error('Organization not found');

  return { user: mapUserRow(user), organization: org };
}

export async function logoutUser(refreshToken?: string) {
  if (!refreshToken) return;
  const db = getSupabaseAdmin();
  await db
    .from('refresh_tokens')
    .update({ revoked_at: new Date().toISOString() })
    .eq('token', refreshToken)
    .is('revoked_at', null);
}

async function revokeAllUserTokens(userId: string) {
  const db = getSupabaseAdmin();
  await db
    .from('refresh_tokens')
    .update({ revoked_at: new Date().toISOString() })
    .eq('user_id', userId)
    .is('revoked_at', null);
}

export async function refreshSession(refreshToken: string) {
  const db = getSupabaseAdmin();
  const { data: row } = await db
    .from('refresh_tokens')
    .select('id, user_id, expires_at, revoked_at')
    .eq('token', refreshToken)
    .maybeSingle();
  if (!row || row.revoked_at || new Date(row.expires_at) < new Date()) {
    throw new Error('Invalid or expired refresh token');
  }

  const { data: user } = await db
    .from('users')
    .select('id, email, first_name, last_name, role, email_verified, organization_id, is_active')
    .eq('id', row.user_id)
    .is('deleted_at', null)
    .eq('is_active', true)
    .single();
  if (!user) throw new Error('Invalid or expired refresh token');

  await db.from('refresh_tokens').update({ revoked_at: new Date().toISOString() }).eq('id', row.id);

  const { data: org } = await db
    .from('organizations')
    .select('id, name, slug')
    .eq('id', user.organization_id)
    .single();
  if (!org) throw new Error('Company not found');

  const tokens = await generateTokens(user);
  return { user: mapUserRow(user), organization: org, tokens };
}

export async function forgotPassword(email: string) {
  const db = getSupabaseAdmin();
  const { data: user } = await db
    .from('users')
    .select('id, email')
    .eq('email', email.toLowerCase())
    .is('deleted_at', null)
    .maybeSingle();
  if (!user) return { message: 'If the email exists, a reset link was sent' };

  const token = secureToken();
  await db.from('password_resets').insert({
    token,
    user_id: user.id,
    expires_at: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
  });
  await sendPasswordResetEmail(user.email, token);
  return { message: 'If the email exists, a reset link was sent' };
}

export async function resetPassword(token: string, password: string) {
  const db = getSupabaseAdmin();
  const { data: reset } = await db
    .from('password_resets')
    .select('*')
    .eq('token', token)
    .is('used_at', null)
    .maybeSingle();
  if (!reset || new Date(reset.expires_at) < new Date()) {
    throw new Error('Invalid or expired token');
  }

  const passwordHash = await bcrypt.hash(password, BCRYPT_ROUNDS);
  await db.from('users').update({ password_hash: passwordHash }).eq('id', reset.user_id);
  await db.from('password_resets').update({ used_at: new Date().toISOString() }).eq('id', reset.id);
  await revokeAllUserTokens(reset.user_id);
}

export async function inviteMember(
  organizationId: string,
  invitedById: string,
  data: { email: string; role: string },
) {
  const role = data.role as UserRole;
  if (!INVITABLE_ROLES.includes(role)) {
    throw new Error('Cannot invite users with this role');
  }

  const db = getSupabaseAdmin();
  const email = data.email.toLowerCase();

  const { data: inviter } = await db
    .from('users')
    .select('first_name, last_name, email, organization_id')
    .eq('id', invitedById)
    .eq('organization_id', organizationId)
    .single();
  if (!inviter) throw new Error('Inviter not found');

  const { data: existingUser } = await db
    .from('users')
    .select('id')
    .eq('email', email)
    .eq('organization_id', organizationId)
    .is('deleted_at', null)
    .maybeSingle();
  if (existingUser) throw new Error('User already exists in this company');

  const { data: pending } = await db
    .from('team_invitations')
    .select('id')
    .eq('email', email)
    .eq('organization_id', organizationId)
    .eq('status', 'PENDING')
    .gt('expires_at', new Date().toISOString())
    .maybeSingle();
  if (pending) throw new Error('Invitation already pending for this email');

  const { data: org } = await db
    .from('organizations')
    .select('name')
    .eq('id', organizationId)
    .single();

  const token = secureToken();
  const { data: invitation, error } = await db
    .from('team_invitations')
    .insert({
      email,
      role,
      token,
      organization_id: organizationId,
      invited_by_id: invitedById,
      expires_at: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString(),
    })
    .select('id, email, role, expires_at')
    .single();
  if (error || !invitation) throw new Error(error?.message ?? 'Failed to create invitation');

  await sendTeamInvitationEmail(
    email,
    token,
    org?.name ?? 'NetWatch',
    `${inviter.first_name} ${inviter.last_name}`,
  );

  await logAudit({
    organizationId,
    category: 'USER',
    action: `Invited ${email} as ${role}`,
    actor: inviter.email,
    actorUserId: invitedById,
  });

  return {
    message: 'Invitation sent',
    id: invitation.id,
    email: invitation.email,
    role: invitation.role,
    expiresAt: invitation.expires_at,
  };
}

export async function acceptInvitation(data: {
  token: string;
  firstName: string;
  lastName: string;
  password: string;
}) {
  const db = getSupabaseAdmin();
  const { data: invitation } = await db
    .from('team_invitations')
    .select('*')
    .eq('token', data.token)
    .eq('status', 'PENDING')
    .maybeSingle();
  if (!invitation || new Date(invitation.expires_at) < new Date()) {
    throw new Error('Invalid or expired invitation');
  }

  const { data: existingUser } = await db
    .from('users')
    .select('id')
    .eq('email', invitation.email)
    .eq('organization_id', invitation.organization_id)
    .is('deleted_at', null)
    .maybeSingle();
  if (existingUser) throw new Error('User already exists');

  const passwordHash = await bcrypt.hash(data.password, BCRYPT_ROUNDS);
  const { data: user, error: userErr } = await db
    .from('users')
    .insert({
      email: invitation.email,
      password_hash: passwordHash,
      first_name: data.firstName,
      last_name: data.lastName,
      role: invitation.role,
      organization_id: invitation.organization_id,
      email_verified: true,
    })
    .select('id, email, first_name, last_name, role, email_verified, organization_id')
    .single();
  if (userErr || !user) throw new Error(userErr?.message ?? 'Failed to create user');

  await db
    .from('team_invitations')
    .update({ status: 'ACCEPTED', accepted_at: new Date().toISOString() })
    .eq('id', invitation.id);

  const { data: org } = await db
    .from('organizations')
    .select('id, name, slug')
    .eq('id', invitation.organization_id)
    .single();
  if (!org) throw new Error('Company not found');

  const tokens = await generateTokens(user);
  await logAudit({
    organizationId: invitation.organization_id,
    category: 'USER',
    action: `User ${user.email} accepted invitation`,
    actor: user.email,
    actorUserId: user.id,
  });

  return { user: mapUserRow(user), organization: org, tokens };
}

export async function updateTeamMember(
  organizationId: string,
  actorId: string,
  actorRole: UserRole,
  memberId: string,
  patch: { role?: UserRole; isActive?: boolean },
) {
  if (actorId === memberId && patch.isActive === false) {
    throw new Error('You cannot disable your own account');
  }

  const db = getSupabaseAdmin();
  const { data: member } = await db
    .from('users')
    .select('id, role')
    .eq('id', memberId)
    .eq('organization_id', organizationId)
    .is('deleted_at', null)
    .single();
  if (!member) throw new Error('Member not found');

  if (actorId === memberId && patch.role && patch.role !== member.role) {
    throw new Error('You cannot change your own role');
  }

  if (patch.role) {
    const targetRole = patch.role as UserRole;
    if (!canAssignRole(actorRole, targetRole)) {
      throw new Error('You cannot assign this role');
    }
    if (member.role === UserRole.OWNER && actorRole !== UserRole.OWNER) {
      throw new Error('Only an owner can modify another owner');
    }
  }

  const update: Record<string, unknown> = {};
  if (patch.role !== undefined) update.role = patch.role;
  if (patch.isActive !== undefined) update.is_active = patch.isActive;

  const { data: user, error } = await db
    .from('users')
    .update(update)
    .eq('id', memberId)
    .eq('organization_id', organizationId)
    .select('id, role, is_active')
    .single();
  if (error || !user) throw new Error('Failed to update member');

  return { id: user.id, role: user.role, isActive: user.is_active };
}

export async function removeTeamMember(
  organizationId: string,
  actorId: string,
  memberId: string,
) {
  if (actorId === memberId) throw new Error('You cannot remove your own account');

  const db = getSupabaseAdmin();
  const { data: member } = await db
    .from('users')
    .select('id')
    .eq('id', memberId)
    .eq('organization_id', organizationId)
    .is('deleted_at', null)
    .maybeSingle();
  if (!member) throw new Error('Member not found');

  await db
    .from('users')
    .update({ deleted_at: new Date().toISOString(), is_active: false })
    .eq('id', memberId)
    .eq('organization_id', organizationId);
}
