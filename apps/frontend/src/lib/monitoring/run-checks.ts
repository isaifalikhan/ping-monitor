import { getSupabaseAdmin } from '@/lib/db/supabase';
import { mapMonitorRow, type MonitorRow } from '@/lib/db/mappers';
import { executeCheck, isServerlessCheckType } from '@/lib/monitoring/executor';
import {
  getActiveMaintenanceWindows,
  isMonitorInMaintenance,
  shouldPauseAlerts,
  type ActiveMaintenanceWindow,
} from '@/lib/monitoring/maintenance';
import { logAudit } from '@/lib/audit/logger';
import { dispatchAlerts } from '@/lib/alerts/dispatcher';
import { dispatchCheckAlerts } from '@/lib/alerts/evaluate';
import {
  appendTimeline,
  deriveIncidentSeverity,
  timelineEvent,
  type TimelineEntry,
} from '@/lib/incidents/timeline';
import { appendIncidentTimeline } from '@/lib/services/incidents';

const BATCH_SIZE = 10;
const FAILURE_THRESHOLD = 3;
const DEFAULT_LATENCY_THRESHOLD_MS = 1000;

export function mapMonitor(m: MonitorRow) {
  return mapMonitorRow(m);
}

async function calculateUptime(monitorId: string) {
  const db = getSupabaseAdmin();
  const { data: checks } = await db
    .from('monitor_checks')
    .select('status')
    .eq('monitor_id', monitorId)
    .order('created_at', { ascending: false })
    .limit(100);
  if (!checks?.length) return 100;
  const up = checks.filter((c) => c.status === 'UP').length;
  return Math.round((up / checks.length) * 10000) / 100;
}

async function handleIncidentAndAlerts(
  monitor: MonitorRow,
  previousStatus: string,
  currentStatus: string,
  errorMessage?: string,
  pauseAlerts = false,
) {
  const db = getSupabaseAdmin();
  const isFailure = currentStatus === 'DOWN' || currentStatus === 'DEGRADED';
  const orgId = monitor.organization_id;

  if (isFailure) {
    const consecutiveFailures = (monitor.consecutive_failures || 0) + 1;
    if (consecutiveFailures >= FAILURE_THRESHOLD) {
      const { data: open } = await db
        .from('incidents')
        .select('id')
        .eq('monitor_id', monitor.id)
        .eq('organization_id', orgId)
        .neq('status', 'RESOLVED')
        .maybeSingle();

      if (!open) {
        const severity = deriveIncidentSeverity(monitor.criticality, currentStatus);
        const timeline = appendTimeline(
          [],
          timelineEvent(
            'DETECTED',
            `Incident opened — ${monitor.name} is ${currentStatus} after ${FAILURE_THRESHOLD} consecutive failures${errorMessage ? `: ${errorMessage}` : ''}`,
            'System',
          ),
        );
        const { data: incident } = await db
          .from('incidents')
          .insert({
            organization_id: orgId,
            monitor_id: monitor.id,
            status: 'OPEN',
            severity,
            root_cause: errorMessage ?? null,
            timeline,
          })
          .select('id')
          .single();

        if (incident) {
          await logAudit({
            organizationId: orgId,
            category: 'INCIDENT',
            action: `Auto-opened incident for ${monitor.name}`,
            actor: 'System',
            target: incident.id,
          });
          if (!pauseAlerts) {
            const alertResult = await dispatchAlerts({
              organizationId: orgId,
              trigger: 'MONITOR_DOWN',
              monitorId: monitor.id,
              monitorName: monitor.name,
              message: `${monitor.name} is ${currentStatus}: ${errorMessage ?? 'Check failed'}`,
            });
            if (alertResult.sent > 0) {
              await appendIncidentTimeline(
                orgId,
                incident.id,
                timelineEvent(
                  'ALERT_SENT',
                  `Alert sent via ${alertResult.sent} channel${alertResult.sent === 1 ? '' : 's'}`,
                  'System',
                ),
              );
            }
          }
        }
      }
    }

    await db
      .from('monitors')
      .update({ consecutive_failures: consecutiveFailures })
      .eq('id', monitor.id);
    return;
  }

  await db.from('monitors').update({ consecutive_failures: 0 }).eq('id', monitor.id);

  if (currentStatus === 'UP' && previousStatus !== 'UP') {
    const { data: openIncidents } = await db
      .from('incidents')
      .select('*')
      .eq('monitor_id', monitor.id)
      .eq('organization_id', orgId)
      .neq('status', 'RESOLVED');

    const endedAt = new Date();
    for (const inc of openIncidents ?? []) {
      const timeline = appendTimeline(
        inc.timeline as TimelineEntry[],
        timelineEvent('RESOLVED', 'Monitor recovered — incident auto-resolved', 'System'),
      );
      const startedAt = new Date(inc.started_at);
      await db
        .from('incidents')
        .update({
          status: 'RESOLVED',
          ended_at: endedAt.toISOString(),
          duration: Math.floor((endedAt.getTime() - startedAt.getTime()) / 1000),
          timeline,
        })
        .eq('id', inc.id);
    }

    if (openIncidents?.length && !pauseAlerts) {
      const alertResult = await dispatchAlerts({
        organizationId: orgId,
        trigger: 'MONITOR_RECOVERY',
        monitorId: monitor.id,
        monitorName: monitor.name,
        message: `${monitor.name} has recovered`,
      });
      if (alertResult.sent > 0 && openIncidents[0]) {
        await appendIncidentTimeline(
          orgId,
          openIncidents[0].id,
          timelineEvent(
            'ALERT_SENT',
            `Recovery alert sent via ${alertResult.sent} channel${alertResult.sent === 1 ? '' : 's'}`,
            'System',
          ),
        );
      }
    }
  }
}

async function executeWithRetries(
  monitor: MonitorRow,
  latencyThreshold: number,
): Promise<Awaited<ReturnType<typeof executeCheck>>> {
  const attempts = Math.max(1, (monitor.retry_count ?? 0) + 1);
  let lastResult: Awaited<ReturnType<typeof executeCheck>> = {
    status: 'DOWN',
    responseTime: null,
    errorMessage: 'Check failed',
  };
  for (let i = 0; i < attempts; i++) {
    lastResult = await executeCheck(
      monitor.type,
      monitor.target,
      monitor.timeout,
      latencyThreshold,
      monitor.port,
    );
    if (lastResult.status === 'UP') return lastResult;
  }
  return lastResult;
}

export async function runMonitorCheck(monitorId: string, activeMaintenance?: ActiveMaintenanceWindow[]) {
  const maintenanceWindows = activeMaintenance ?? (await getActiveMaintenanceWindows());
  const db = getSupabaseAdmin();
  const { data: monitor } = await db
    .from('monitors')
    .select('*')
    .eq('id', monitorId)
    .is('deleted_at', null)
    .eq('is_active', true)
    .maybeSingle();
  if (!monitor || monitor.maintenance_mode) return null;
  if (!isServerlessCheckType(monitor.type)) return null;
  if (isMonitorInMaintenance(maintenanceWindows, monitor.organization_id, monitor.id)) {
    return null;
  }

  const pauseAlerts = shouldPauseAlerts(
    maintenanceWindows,
    monitor.organization_id,
    monitor.id,
  );

  const { data: settings } = await db
    .from('organization_settings')
    .select('latency_threshold_ms')
    .eq('organization_id', monitor.organization_id)
    .maybeSingle();
  const latencyThreshold = settings?.latency_threshold_ms ?? DEFAULT_LATENCY_THRESHOLD_MS;
  const result = await executeWithRetries(monitor as MonitorRow, latencyThreshold);
  const previousStatus = monitor.status;

  await db.from('monitor_checks').insert({
    monitor_id: monitor.id,
    organization_id: monitor.organization_id,
    status: result.status,
    response_time: result.responseTime,
    error_message: result.errorMessage ?? null,
  });

  const uptimePercent = await calculateUptime(monitor.id);
  await db
    .from('monitors')
    .update({
      status: result.status,
      last_response_time: result.responseTime,
      last_checked_at: new Date().toISOString(),
      uptime_percent: uptimePercent,
    })
    .eq('id', monitor.id);

  await handleIncidentAndAlerts(
    monitor as MonitorRow,
    previousStatus,
    result.status,
    result.errorMessage,
    pauseAlerts,
  );

  await dispatchCheckAlerts({
    organizationId: monitor.organization_id,
    monitorId: monitor.id,
    monitorName: monitor.name,
    monitorType: monitor.type,
    previousStatus,
    currentStatus: result.status,
    errorMessage: result.errorMessage,
    pauseAlerts,
    message: `${monitor.name}: ${result.status}${result.errorMessage ? ` — ${result.errorMessage}` : ''}${result.responseTime != null ? ` (${result.responseTime}ms)` : ''}`,
  });

  return result;
}

export async function runDueChecks() {
  const db = getSupabaseAdmin();
  const now = Date.now();
  const activeMaintenance = await getActiveMaintenanceWindows();

  const { data: monitors } = await db
    .from('monitors')
    .select('*')
    .eq('is_active', true)
    .is('deleted_at', null)
    .eq('maintenance_mode', false)
    .order('last_checked_at', { ascending: true, nullsFirst: true })
    .limit(BATCH_SIZE * 3);

  const due = (monitors ?? []).filter((m) => {
    if (!isServerlessCheckType(m.type)) return false;
    if (isMonitorInMaintenance(activeMaintenance, m.organization_id, m.id)) return false;
    if (!m.last_checked_at) return true;
    return now - new Date(m.last_checked_at).getTime() >= m.check_interval * 1000;
  }).slice(0, BATCH_SIZE);

  let processed = 0;
  let skipped = 0;
  for (const monitor of due) {
    try {
      const result = await runMonitorCheck(monitor.id, activeMaintenance);
      if (result) processed++;
      else skipped++;
    } catch (err) {
      console.error(`Check failed for ${monitor.id}`, err);
      skipped++;
    }
  }
  return { processed, skipped, total: due.length, retention: await purgeRetention(db) };
}

async function purgeRetention(db: ReturnType<typeof import('@/lib/db/supabase').getSupabaseAdmin>) {
  const { purgeExpiredCheckData } = await import('@/lib/settings/enforce');
  return purgeExpiredCheckData(db);
}
