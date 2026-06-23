import { getSupabaseAdmin } from '@/lib/db/supabase';

export type ActiveMaintenanceWindow = {
  organizationId: string;
  monitorId: string | null;
  pauseAlerts: boolean;
};

/** Active maintenance windows (start <= now <= end). */
export async function getActiveMaintenanceWindows(): Promise<ActiveMaintenanceWindow[]> {
  const db = getSupabaseAdmin();
  const now = new Date().toISOString();
  const { data } = await db
    .from('maintenance_windows')
    .select('organization_id, monitor_id, pause_alerts')
    .lte('start_time', now)
    .gte('end_time', now);

  return (data ?? []).map((w) => ({
    organizationId: w.organization_id,
    monitorId: w.monitor_id,
    pauseAlerts: w.pause_alerts,
  }));
}

export function isMonitorInMaintenance(
  windows: ActiveMaintenanceWindow[],
  organizationId: string,
  monitorId: string,
): boolean {
  return windows.some(
    (w) =>
      w.organizationId === organizationId &&
      (w.monitorId === null || w.monitorId === monitorId),
  );
}

export function shouldPauseAlerts(
  windows: ActiveMaintenanceWindow[],
  organizationId: string,
  monitorId: string,
): boolean {
  return windows.some(
    (w) =>
      w.organizationId === organizationId &&
      w.pauseAlerts &&
      (w.monitorId === null || w.monitorId === monitorId),
  );
}
