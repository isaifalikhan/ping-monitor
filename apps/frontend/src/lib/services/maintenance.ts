import { getSupabaseAdmin } from '@/lib/db/supabase';
import { resolveMaintenanceStatus } from '@/lib/maintenance/utils';
import type { MaintenanceWindow } from '@/lib/types';
import { createMaintenanceSchema } from '@/lib/validations/api-maintenance';

function mapMaintenanceRow(
  m: {
    id: string;
    title: string;
    start_time: string;
    end_time: string;
    reason: string | null;
    pause_alerts: boolean;
    created_at: string;
    monitor_id: string | null;
    monitors?: { id: string; name: string } | { id: string; name: string }[] | null;
  },
  now = new Date(),
): MaintenanceWindow {
  const monitorRow = Array.isArray(m.monitors) ? m.monitors[0] : m.monitors;
  return {
    id: m.id,
    title: m.title,
    monitor: monitorRow ? { id: monitorRow.id, name: monitorRow.name } : null,
    startTime: m.start_time,
    endTime: m.end_time,
    reason: m.reason,
    pauseAlerts: m.pause_alerts,
    status: resolveMaintenanceStatus(m.start_time, m.end_time, now),
    createdAt: m.created_at,
  };
}

export async function listMaintenanceWindows(organizationId: string) {
  const db = getSupabaseAdmin();
  const { data: items } = await db
    .from('maintenance_windows')
    .select('*, monitors(id, name)')
    .eq('organization_id', organizationId)
    .order('start_time', { ascending: false });

  return (items ?? []).map((m) => mapMaintenanceRow(m));
}

export async function getActiveMaintenanceWindows(organizationId: string) {
  const windows = await listMaintenanceWindows(organizationId);
  return windows.filter((w) => w.status === 'ACTIVE');
}

export async function createMaintenanceWindow(
  organizationId: string,
  raw: Record<string, unknown>,
) {
  const data = createMaintenanceSchema.parse(raw);
  const db = getSupabaseAdmin();

  if (data.monitorId) {
    const { data: monitor } = await db
      .from('monitors')
      .select('id')
      .eq('id', data.monitorId)
      .eq('organization_id', organizationId)
      .is('deleted_at', null)
      .maybeSingle();
    if (!monitor) throw new Error('Monitor not found');
  }

  const { data: item, error } = await db
    .from('maintenance_windows')
    .insert({
      organization_id: organizationId,
      title: data.title,
      monitor_id: data.monitorId ?? null,
      start_time: new Date(data.startTime).toISOString(),
      end_time: new Date(data.endTime).toISOString(),
      reason: data.reason ?? null,
      pause_alerts: data.pauseAlerts,
    })
    .select('*, monitors(id, name)')
    .single();

  if (error || !item) throw new Error(error?.message ?? 'Failed to create maintenance window');
  return mapMaintenanceRow(item);
}

export async function cancelMaintenanceWindow(organizationId: string, id: string) {
  const db = getSupabaseAdmin();
  const { data: existing } = await db
    .from('maintenance_windows')
    .select('*')
    .eq('id', id)
    .eq('organization_id', organizationId)
    .maybeSingle();

  if (!existing) throw new Error('Maintenance window not found');

  const status = resolveMaintenanceStatus(existing.start_time, existing.end_time);
  if (status === 'COMPLETED') throw new Error('Completed maintenance windows cannot be cancelled');

  if (status === 'UPCOMING') {
    const { error } = await db.from('maintenance_windows').delete().eq('id', id);
    if (error) throw new Error(error.message);
    return { id, action: 'deleted' as const };
  }

  const now = new Date().toISOString();
  const { error } = await db
    .from('maintenance_windows')
    .update({ end_time: now, updated_at: now })
    .eq('id', id);
  if (error) throw new Error(error.message);
  return { id, action: 'ended' as const };
}
