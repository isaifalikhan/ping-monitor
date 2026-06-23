import { DEFAULT_SETTINGS_PREFS, type OrganizationSettingsPrefs } from '@/lib/settings/types';

/** Comma-separated IPs; empty string allows all. */
export function parseIpAllowlist(allowlist: string): string[] {
  return allowlist
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean);
}

export function isIpAllowed(clientIp: string, allowlist: string): boolean {
  const rules = parseIpAllowlist(allowlist);
  if (!rules.length) return true;
  const ip = clientIp.trim();
  return rules.some((rule) => rule === ip);
}

export function getClientIp(headerStore: { get(name: string): string | null }): string {
  const forwarded = headerStore.get('x-forwarded-for');
  if (forwarded) return forwarded.split(',')[0].trim();
  return headerStore.get('x-real-ip') ?? headerStore.get('x-client-ip') ?? '127.0.0.1';
}

export function clampSessionMinutes(minutes: number): number {
  return Math.max(15, Math.min(1440, Math.round(minutes)));
}

/** Access token TTL — capped at 60 minutes for security. */
export function accessTokenExpiresIn(sessionTimeoutMinutes: number): string {
  const minutes = Math.min(60, clampSessionMinutes(sessionTimeoutMinutes));
  return `${minutes}m`;
}

export function refreshTokenExpiresAt(sessionTimeoutMinutes: number): Date {
  const minutes = clampSessionMinutes(sessionTimeoutMinutes);
  return new Date(Date.now() + minutes * 60_000);
}

export function mergePreferences(raw?: object | null): OrganizationSettingsPrefs {
  const prefs = { ...DEFAULT_SETTINGS_PREFS, ...(raw ?? {}) } as Record<string, unknown>;
  return {
    defaultCheckInterval: Number(prefs.defaultCheckInterval) || DEFAULT_SETTINGS_PREFS.defaultCheckInterval,
    defaultTimeout: Number(prefs.defaultTimeout) || DEFAULT_SETTINGS_PREFS.defaultTimeout,
    retentionDays: Number(prefs.retentionDays) || DEFAULT_SETTINGS_PREFS.retentionDays,
    statusPageSlug: String(prefs.statusPageSlug ?? ''),
    sessionTimeoutMinutes:
      Number(prefs.sessionTimeoutMinutes) || DEFAULT_SETTINGS_PREFS.sessionTimeoutMinutes,
    requireMfa: Boolean(prefs.requireMfa),
    ipAllowlist: String(prefs.ipAllowlist ?? ''),
    brandPrimaryColor: String(prefs.brandPrimaryColor || DEFAULT_SETTINGS_PREFS.brandPrimaryColor),
    brandLogoUrl: String(prefs.brandLogoUrl ?? ''),
    slackWebhook: String(prefs.slackWebhook ?? ''),
    pagerdutyKey: String(prefs.pagerdutyKey ?? ''),
    datadogApiKey: String(prefs.datadogApiKey ?? ''),
  };
}

export async function purgeExpiredCheckData(
  db: ReturnType<typeof import('@/lib/db/supabase').getSupabaseAdmin>,
) {
  const { data: rows } = await db.from('organization_settings').select('organization_id, preferences');
  let purged = 0;

  for (const row of rows ?? []) {
    const prefs = mergePreferences(row.preferences as object);
    const cutoff = new Date(Date.now() - prefs.retentionDays * 86_400_000).toISOString();
    const { data: deleted } = await db
      .from('monitor_checks')
      .delete()
      .eq('organization_id', row.organization_id)
      .lt('created_at', cutoff)
      .select('id');
    purged += deleted?.length ?? 0;
  }

  return { purged };
}
