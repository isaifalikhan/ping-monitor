import { slugify } from '@netwatch/shared';
import { getSupabaseAdmin } from '@/lib/db/supabase';
import { logAudit } from '@/lib/audit/logger';
import { updateSettingsSchema } from '@/lib/validations/api-settings';
import { mergePreferences } from '@/lib/settings/enforce';
import { DEFAULT_SETTINGS_PREFS, type OrganizationSettings, type SettingsResponse } from '@/lib/settings/types';

function mapSettingsRow(
  settings: {
    latency_threshold_ms: number;
    notification_email: string | null;
    timezone: string;
    enable_public_status_page: boolean;
    preferences: unknown;
  } | null,
  orgSlug: string,
): OrganizationSettings {
  const prefs = mergePreferences(settings?.preferences as object);
  return {
    latencyThresholdMs: settings?.latency_threshold_ms ?? 1000,
    notificationEmail: settings?.notification_email ?? '',
    timezone: settings?.timezone ?? 'UTC',
    enablePublicStatusPage: settings?.enable_public_status_page ?? true,
    ...prefs,
    statusPageSlug: prefs.statusPageSlug || orgSlug,
  };
}

export async function loadOrganizationSettings(organizationId: string): Promise<OrganizationSettings> {
  const db = getSupabaseAdmin();
  const [{ data: settings }, { data: org }] = await Promise.all([
    db.from('organization_settings').select('*').eq('organization_id', organizationId).maybeSingle(),
    db.from('organizations').select('slug').eq('id', organizationId).single(),
  ]);
  return mapSettingsRow(settings, org?.slug ?? '');
}

export async function getSettings(organizationId: string): Promise<SettingsResponse> {
  const db = getSupabaseAdmin();
  let { data: settings } = await db
    .from('organization_settings')
    .select('*')
    .eq('organization_id', organizationId)
    .maybeSingle();

  if (!settings) {
    await db.from('organization_settings').insert({ organization_id: organizationId });
    const res = await db
      .from('organization_settings')
      .select('*')
      .eq('organization_id', organizationId)
      .single();
    settings = res.data;
  }

  const { data: org } = await db
    .from('organizations')
    .select('id, name, slug')
    .eq('id', organizationId)
    .single();

  return {
    organization: org ? { id: org.id, name: org.name, slug: org.slug } : null,
    settings: mapSettingsRow(settings, org?.slug ?? ''),
  };
}

export async function updateSettings(
  organizationId: string,
  actor: string,
  raw: Record<string, unknown>,
  meta?: { ipAddress?: string; actorUserId?: string },
): Promise<SettingsResponse> {
  const dto = updateSettingsSchema.parse(raw);
  const db = getSupabaseAdmin();

  const { data: existing } = await db
    .from('organization_settings')
    .select('*')
    .eq('organization_id', organizationId)
    .maybeSingle();

  const currentPrefs =
    typeof existing?.preferences === 'object' && existing?.preferences
      ? { ...existing.preferences }
      : { ...DEFAULT_SETTINGS_PREFS };

  const mergedPrefs = dto.preferences
    ? { ...currentPrefs, ...dto.preferences }
    : currentPrefs;

  const patch: Record<string, unknown> = { preferences: mergedPrefs };
  if (dto.latencyThresholdMs !== undefined) patch.latency_threshold_ms = dto.latencyThresholdMs;
  if (dto.notificationEmail !== undefined) patch.notification_email = dto.notificationEmail || null;
  if (dto.timezone !== undefined) patch.timezone = dto.timezone;
  if (dto.enablePublicStatusPage !== undefined) {
    patch.enable_public_status_page = dto.enablePublicStatusPage;
  }

  if (dto.preferences?.statusPageSlug !== undefined) {
    const newSlug = slugify(dto.preferences.statusPageSlug);
    if (!newSlug) throw new Error('Invalid status page slug');

    const { data: conflict } = await db
      .from('organizations')
      .select('id')
      .eq('slug', newSlug)
      .neq('id', organizationId)
      .is('deleted_at', null)
      .maybeSingle();
    if (conflict) throw new Error('Status page slug is already in use');

    await db.from('organizations').update({ slug: newSlug }).eq('id', organizationId);
    await db
      .from('status_pages')
      .upsert({ organization_id: organizationId, slug: newSlug, enabled: true });
    mergedPrefs.statusPageSlug = newSlug;
    patch.preferences = mergedPrefs;
  }

  await db.from('organization_settings').upsert({ organization_id: organizationId, ...patch });

  await logAudit({
    organizationId,
    category: 'SETTINGS',
    action: 'Updated company settings',
    actor,
    actorUserId: meta?.actorUserId,
    target: 'Company settings',
    ipAddress: meta?.ipAddress,
  });

  return getSettings(organizationId);
}

export async function getNotificationEmailFallback(organizationId: string): Promise<string | null> {
  const db = getSupabaseAdmin();
  const { data } = await db
    .from('organization_settings')
    .select('notification_email')
    .eq('organization_id', organizationId)
    .maybeSingle();
  const email = data?.notification_email?.trim();
  return email || null;
}
