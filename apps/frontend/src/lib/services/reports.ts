import { getSupabaseAdmin } from '@/lib/db/supabase';

export type ReportSummaryData = {
  summary: {
    totalMonitors: number;
    avgUptime: number;
    avgResponseTime: number;
    totalIncidents: number;
    openIncidents: number;
    resolvedIncidents: number;
  };
  responseTrend: { day: string; ms: number }[];
  incidentTrend: { day: string; count: number }[];
  monitors: { name: string; uptime: number; status: string; category: string | null }[];
  period: { from: string; to: string };
};

type CheckRow = {
  monitor_id: string;
  status: string;
  response_time: number | null;
  created_at: string;
};

function startOfDay(date: Date) {
  const d = new Date(date);
  d.setHours(0, 0, 0, 0);
  return d;
}

function endOfDay(date: Date) {
  const d = new Date(date);
  d.setHours(23, 59, 59, 999);
  return d;
}

function resolveDateRange(from?: string, to?: string) {
  const toDate = to ? endOfDay(new Date(to)) : new Date();
  const fromDate = from ? startOfDay(new Date(from)) : startOfDay(new Date(toDate.getTime() - 30 * 86_400_000));
  return { fromDate, toDate };
}

function eachDay(from: Date, to: Date) {
  const days: string[] = [];
  const cur = startOfDay(from);
  const end = startOfDay(to);
  while (cur <= end) {
    days.push(cur.toISOString().slice(0, 10));
    cur.setDate(cur.getDate() + 1);
  }
  return days;
}

function uptimePercent(checks: { status: string }[]) {
  if (!checks.length) return 100;
  const up = checks.filter((c) => c.status === 'UP').length;
  return Math.round((up / checks.length) * 10000) / 100;
}

function buildResponseTrend(checks: CheckRow[], from: Date, to: Date) {
  const days = eachDay(from, to);
  const byDay = new Map<string, number[]>();

  for (const c of checks) {
    if (c.response_time == null) continue;
    const day = c.created_at.slice(0, 10);
    const bucket = byDay.get(day) ?? [];
    bucket.push(c.response_time);
    byDay.set(day, bucket);
  }

  return days.map((day) => {
    const values = byDay.get(day);
    return {
      day,
      ms: values?.length
        ? Math.round(values.reduce((sum, v) => sum + v, 0) / values.length)
        : 0,
    };
  });
}

function buildIncidentTrend(incidents: { started_at: string }[], from: Date, to: Date) {
  const days = eachDay(from, to);
  return days.map((day) => ({
    day,
    count: incidents.filter((i) => i.started_at.slice(0, 10) === day).length,
  }));
}

export async function getReportSummary(
  organizationId: string,
  from?: string,
  to?: string,
): Promise<ReportSummaryData> {
  const { fromDate, toDate } = resolveDateRange(from, to);
  const fromIso = fromDate.toISOString();
  const toIso = toDate.toISOString();
  const db = getSupabaseAdmin();

  const [{ data: monitors }, { data: incidents }, { data: checks }] = await Promise.all([
    db
      .from('monitors')
      .select('id, name, status, category, uptime_percent')
      .eq('organization_id', organizationId)
      .is('deleted_at', null),
    db
      .from('incidents')
      .select('status, started_at')
      .eq('organization_id', organizationId)
      .gte('started_at', fromIso)
      .lte('started_at', toIso),
    db
      .from('monitor_checks')
      .select('monitor_id, status, response_time, created_at')
      .eq('organization_id', organizationId)
      .gte('created_at', fromIso)
      .lte('created_at', toIso)
      .order('created_at', { ascending: true }),
  ]);

  const monitorList = monitors ?? [];
  const incidentList = incidents ?? [];
  const checkList = (checks ?? []) as CheckRow[];

  const checksByMonitor = new Map<string, CheckRow[]>();
  for (const check of checkList) {
    const bucket = checksByMonitor.get(check.monitor_id) ?? [];
    bucket.push(check);
    checksByMonitor.set(check.monitor_id, bucket);
  }

  const monitorRows = monitorList.map((m) => {
    const periodChecks = checksByMonitor.get(m.id) ?? [];
    const uptime =
      periodChecks.length > 0 ? uptimePercent(periodChecks) : (m.uptime_percent as number);
    return {
      name: m.name as string,
      uptime,
      status: m.status as string,
      category: (m.category as string | null) ?? null,
    };
  });

  const uptimesWithData = monitorRows.filter((m) => m.uptime != null);
  const avgUptime =
    uptimesWithData.length > 0
      ? Math.round(
          (uptimesWithData.reduce((sum, m) => sum + m.uptime, 0) / uptimesWithData.length) * 100,
        ) / 100
      : 100;

  const responseTimes = checkList
    .filter((c) => c.response_time != null)
    .map((c) => c.response_time as number);
  const avgResponseTime =
    responseTimes.length > 0
      ? Math.round(responseTimes.reduce((a, b) => a + b, 0) / responseTimes.length)
      : 0;

  const openIncidents = incidentList.filter((i) => i.status !== 'RESOLVED').length;
  const resolvedIncidents = incidentList.filter((i) => i.status === 'RESOLVED').length;

  return {
    summary: {
      totalMonitors: monitorList.length,
      avgUptime,
      avgResponseTime,
      totalIncidents: incidentList.length,
      openIncidents,
      resolvedIncidents,
    },
    responseTrend: buildResponseTrend(checkList, fromDate, toDate),
    incidentTrend: buildIncidentTrend(incidentList, fromDate, toDate),
    monitors: monitorRows.sort((a, b) => a.name.localeCompare(b.name)),
    period: {
      from: fromDate.toISOString().slice(0, 10),
      to: toDate.toISOString().slice(0, 10),
    },
  };
}
