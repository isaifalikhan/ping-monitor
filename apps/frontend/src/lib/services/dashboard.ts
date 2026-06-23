import { getSupabaseAdmin } from '@/lib/db/supabase';
import { MONITOR_CATEGORIES } from '@/lib/validations/monitor';

const DAY_LABELS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;

const CATEGORY_COLORS: Record<string, string> = {
  ERP: 'hsl(var(--primary))',
  Server: 'hsl(142 76% 36%)',
  Switch: 'hsl(38 92% 50%)',
  Router: 'hsl(199 89% 48%)',
  Firewall: 'hsl(0 72% 51%)',
  Database: 'hsl(280 70% 50%)',
  Website: 'hsl(262 83% 58%)',
  API: 'hsl(173 58% 39%)',
  DNS: 'hsl(45 93% 47%)',
  SSL: 'hsl(215 20% 55%)',
  Other: 'hsl(220 9% 46%)',
};

type CheckRow = { status: string; response_time: number | null; created_at: string };
type IncidentRow = { started_at: string };

function startOfDay(date: Date) {
  const d = new Date(date);
  d.setHours(0, 0, 0, 0);
  return d;
}

function formatHourLabel(date: Date) {
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function buildResponseTimeTrend(checks: CheckRow[]) {
  const now = Date.now();
  const bucketMs = 2 * 3_600_000;
  const buckets = 12;
  const result: { time: string; ms: number }[] = [];

  for (let i = buckets - 1; i >= 0; i--) {
    const bucketEnd = now - i * bucketMs;
    const bucketStart = bucketEnd - bucketMs;
    const inBucket = checks.filter((c) => {
      const t = new Date(c.created_at).getTime();
      return t >= bucketStart && t < bucketEnd && c.response_time != null;
    });
    const avg =
      inBucket.length > 0
        ? Math.round(inBucket.reduce((sum, c) => sum + (c.response_time ?? 0), 0) / inBucket.length)
        : 0;
    result.push({
      time: i === 0 ? 'Now' : formatHourLabel(new Date(bucketEnd)),
      ms: avg,
    });
  }
  return result;
}

function uptimePercent(checks: CheckRow[]) {
  if (!checks.length) return 100;
  const up = checks.filter((c) => c.status === 'UP').length;
  return Math.round((up / checks.length) * 10000) / 100;
}

function buildUptimeTrend(checks: CheckRow[]) {
  const today = startOfDay(new Date());
  const result: { day: string; uptime: number }[] = [];

  for (let i = 6; i >= 0; i--) {
    const dayStart = new Date(today);
    dayStart.setDate(dayStart.getDate() - i);
    const dayEnd = new Date(dayStart);
    dayEnd.setDate(dayEnd.getDate() + 1);
    const dayChecks = checks.filter((c) => {
      const t = new Date(c.created_at);
      return t >= dayStart && t < dayEnd;
    });
    result.push({
      day: DAY_LABELS[dayStart.getDay()],
      uptime: uptimePercent(dayChecks),
    });
  }
  return result;
}

function buildUptime24h(checks: CheckRow[]) {
  const now = Date.now();
  const bucketMs = 2 * 3_600_000;
  const buckets = 12;
  const result: { hour: string; uptime: number }[] = [];

  for (let i = buckets - 1; i >= 0; i--) {
    const bucketEnd = now - i * bucketMs;
    const bucketStart = bucketEnd - bucketMs;
    const inBucket = checks.filter((c) => {
      const t = new Date(c.created_at).getTime();
      return t >= bucketStart && t < bucketEnd;
    });
    result.push({
      hour: i === 0 ? 'Now' : formatHourLabel(new Date(bucketEnd)),
      uptime: uptimePercent(inBucket),
    });
  }
  return result;
}

function buildIncidentTrend(incidents: IncidentRow[]) {
  const today = startOfDay(new Date());
  const result: { day: string; incidents: number }[] = [];

  for (let i = 6; i >= 0; i--) {
    const dayStart = new Date(today);
    dayStart.setDate(dayStart.getDate() - i);
    const dayEnd = new Date(dayStart);
    dayEnd.setDate(dayEnd.getDate() + 1);
    const count = incidents.filter((inc) => {
      const t = new Date(inc.started_at);
      return t >= dayStart && t < dayEnd;
    }).length;
    result.push({ day: DAY_LABELS[dayStart.getDay()], incidents: count });
  }
  return result;
}

function buildCategoryDistribution(
  monitors: { category: string | null; status: string }[],
) {
  const counts: Record<string, number> = {};
  for (const m of monitors) {
    const cat = m.category || 'Other';
    counts[cat] = (counts[cat] ?? 0) + 1;
  }

  return MONITOR_CATEGORIES.map((name) => ({
    name,
    value: counts[name] ?? 0,
    color: CATEGORY_COLORS[name] ?? CATEGORY_COLORS.Other,
  })).filter((d) => d.value > 0);
}

export async function getDashboardStats(organizationId: string) {
  const db = getSupabaseAdmin();
  const dayAgo = new Date(Date.now() - 86_400_000).toISOString();

  const [{ data: monitors }, { data: incidents }, { count: totalMonitors }, { data: recentChecks }] =
    await Promise.all([
      db
        .from('monitors')
        .select('status, uptime_percent')
        .eq('organization_id', organizationId)
        .is('deleted_at', null)
        .eq('is_active', true),
      db
        .from('incidents')
        .select('id')
        .eq('organization_id', organizationId)
        .neq('status', 'RESOLVED'),
      db
        .from('monitors')
        .select('*', { count: 'exact', head: true })
        .eq('organization_id', organizationId)
        .is('deleted_at', null),
      db
        .from('monitor_checks')
        .select('response_time')
        .eq('organization_id', organizationId)
        .gte('created_at', dayAgo)
        .not('response_time', 'is', null),
    ]);

  const list = monitors ?? [];
  const online = list.filter((m) => m.status === 'UP').length;
  const offline = list.filter((m) => m.status === 'DOWN').length;
  const degraded = list.filter((m) => m.status === 'DEGRADED').length;
  const unknown = list.filter((m) => m.status === 'UNKNOWN').length;

  const responseTimes = (recentChecks ?? []).map((c) => c.response_time as number);
  const avgResponseTime =
    responseTimes.length > 0
      ? Math.round(responseTimes.reduce((a, b) => a + b, 0) / responseTimes.length)
      : 0;

  const uptimePercent =
    list.length > 0
      ? Math.round((list.reduce((a, m) => a + m.uptime_percent, 0) / list.length) * 100) / 100
      : 100;

  return {
    totalMonitors: totalMonitors ?? 0,
    online,
    offline,
    degraded,
    unknown,
    activeIncidents: incidents?.length ?? 0,
    uptimePercent,
    avgResponseTime,
  };
}

export async function getDashboardCharts(organizationId: string) {
  const db = getSupabaseAdmin();
  const now = Date.now();
  const dayAgo = new Date(now - 86_400_000).toISOString();
  const weekAgo = new Date(now - 7 * 86_400_000).toISOString();

  const [{ data: monitors }, { data: checks24h }, { data: checks7d }, { data: incidents }] =
    await Promise.all([
      db
        .from('monitors')
        .select('status, category')
        .eq('organization_id', organizationId)
        .is('deleted_at', null)
        .eq('is_active', true),
      db
        .from('monitor_checks')
        .select('status, response_time, created_at')
        .eq('organization_id', organizationId)
        .gte('created_at', dayAgo)
        .order('created_at', { ascending: true }),
      db
        .from('monitor_checks')
        .select('status, response_time, created_at')
        .eq('organization_id', organizationId)
        .gte('created_at', weekAgo)
        .order('created_at', { ascending: true }),
      db
        .from('incidents')
        .select('started_at')
        .eq('organization_id', organizationId)
        .gte('started_at', weekAgo),
    ]);

  const list = monitors ?? [];
  const online = list.filter((m) => m.status === 'UP').length;
  const offline = list.filter((m) => m.status === 'DOWN').length;
  const degraded = list.filter((m) => m.status === 'DEGRADED').length;
  const unknown = list.filter((m) => m.status === 'UNKNOWN').length;

  return {
    responseTimeTrend: buildResponseTimeTrend((checks24h ?? []) as CheckRow[]),
    uptimeTrend: buildUptimeTrend((checks7d ?? []) as CheckRow[]),
    uptime24h: buildUptime24h((checks24h ?? []) as CheckRow[]),
    incidentTrend: buildIncidentTrend((incidents ?? []) as IncidentRow[]),
    categoryDistribution: buildCategoryDistribution(list),
    /** @deprecated use categoryDistribution — kept for backwards compatibility */
    monitorDistribution: buildCategoryDistribution(list),
    statusDistribution: [
      { name: 'Online', value: online, color: 'hsl(142 76% 36%)' },
      { name: 'Degraded', value: degraded, color: 'hsl(38 92% 50%)' },
      { name: 'Offline', value: offline, color: 'hsl(var(--destructive))' },
      { name: 'Unknown', value: unknown, color: 'hsl(220 9% 46%)' },
    ].filter((d) => d.value > 0),
  };
}

export async function getRecentAlerts(organizationId: string) {
  const db = getSupabaseAdmin();
  const { data: logs } = await db
    .from('alert_logs')
    .select('*, alert_channels(channel), monitors(name)')
    .eq('organization_id', organizationId)
    .order('created_at', { ascending: false })
    .limit(6);

  return (logs ?? []).map((l) => ({
    id: l.id,
    monitor: (l.monitors as { name?: string } | null)?.name ?? 'System',
    type: l.trigger.replace(/_/g, ' '),
    channel: (l.alert_channels as { channel?: string })?.channel ?? 'EMAIL',
    time: new Date(l.created_at).toLocaleString([], {
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    }),
    status: l.status === 'SENT' ? 'sent' : 'failed',
  }));
}
