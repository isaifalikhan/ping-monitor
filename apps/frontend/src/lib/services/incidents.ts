import { getSupabaseAdmin } from '@/lib/db/supabase';
import { logAudit } from '@/lib/audit/logger';
import {
  appendTimeline,
  deriveIncidentSeverity,
  mapTimelineForApi,
  timelineEvent,
  type TimelineEntry,
} from '@/lib/incidents/timeline';

export type IncidentRow = {
  id: string;
  organization_id: string;
  monitor_id: string;
  status: string;
  severity: string;
  started_at: string;
  ended_at?: string | null;
  duration?: number | null;
  root_cause?: string | null;
  notes?: string | null;
  assigned_user_id?: string | null;
  timeline?: TimelineEntry[] | null;
};

async function getActorDisplayName(userId: string, fallback: string): Promise<string> {
  const db = getSupabaseAdmin();
  const { data } = await db
    .from('users')
    .select('first_name, last_name, email')
    .eq('id', userId)
    .maybeSingle();
  if (!data) return fallback;
  const name = `${data.first_name} ${data.last_name}`.trim();
  return name || data.email || fallback;
}

function mapIncidentBase(i: IncidentRow) {
  return {
    id: i.id,
    status: i.status,
    severity: i.severity,
    startedAt: i.started_at,
    endedAt: i.ended_at ?? null,
    duration: i.duration,
    rootCause: i.root_cause,
    notes: i.notes,
    assignedUserId: i.assigned_user_id ?? null,
    timeline: mapTimelineForApi(i.timeline, i.started_at),
    monitor: { id: i.monitor_id, name: '' },
  };
}

async function loadAssignedUsers(incidents: IncidentRow[]) {
  const userIds = [...new Set(incidents.map((i) => i.assigned_user_id).filter(Boolean))] as string[];
  if (!userIds.length) return {} as Record<string, { id: string; firstName: string; lastName: string; email: string }>;
  const db = getSupabaseAdmin();
  const { data: users } = await db
    .from('users')
    .select('id, first_name, last_name, email')
    .in('id', userIds);
  return Object.fromEntries(
    (users ?? []).map((u) => [
      u.id,
      { id: u.id, firstName: u.first_name, lastName: u.last_name, email: u.email },
    ]),
  );
}

export async function enrichIncidents(incidents: IncidentRow[]) {
  if (!incidents.length) return [];
  const db = getSupabaseAdmin();
  const monitorIds = [...new Set(incidents.map((i) => i.monitor_id))];
  const [{ data: monitors }, userMap] = await Promise.all([
    db.from('monitors').select('id, name, target').in('id', monitorIds),
    loadAssignedUsers(incidents),
  ]);
  const monitorMap = Object.fromEntries((monitors ?? []).map((m) => [m.id, m]));

  return incidents.map((i) => {
    const base = mapIncidentBase(i);
    const mon = monitorMap[i.monitor_id];
    return {
      ...base,
      title: mon?.name ? `${mon.name} incident` : undefined,
      monitor: {
        id: i.monitor_id,
        name: mon?.name ?? 'Unknown',
        target: mon?.target,
      },
      assignedUser: i.assigned_user_id ? userMap[i.assigned_user_id] ?? null : null,
    };
  });
}

export async function listIncidents(organizationId: string, status?: string) {
  const db = getSupabaseAdmin();
  let q = db
    .from('incidents')
    .select('*')
    .eq('organization_id', organizationId)
    .order('started_at', { ascending: false })
    .limit(100);
  if (status) q = q.eq('status', status);
  const { data: incidents } = await q;
  return enrichIncidents((incidents ?? []) as IncidentRow[]);
}

export async function getIncident(organizationId: string, id: string) {
  const db = getSupabaseAdmin();
  const { data: incident } = await db
    .from('incidents')
    .select('*')
    .eq('id', id)
    .eq('organization_id', organizationId)
    .single();
  if (!incident) throw new Error('Incident not found');
  const [enriched] = await enrichIncidents([incident as IncidentRow]);
  return enriched;
}

export async function acknowledgeIncident(
  organizationId: string,
  id: string,
  userId: string,
  actorFallback: string,
) {
  const db = getSupabaseAdmin();
  const { data: incident } = await db
    .from('incidents')
    .select('*')
    .eq('id', id)
    .eq('organization_id', organizationId)
    .single();
  if (!incident) throw new Error('Incident not found');
  if (incident.status === 'RESOLVED') throw new Error('Incident is already resolved');
  if (incident.status === 'ACKNOWLEDGED') throw new Error('Incident is already acknowledged');

  const actor = await getActorDisplayName(userId, actorFallback);
  const events = [timelineEvent('ACKNOWLEDGED', `Acknowledged by ${actor}`, actor)];
  const patch: Record<string, unknown> = { status: 'ACKNOWLEDGED' };

  if (!incident.assigned_user_id) {
    patch.assigned_user_id = userId;
    events.push(timelineEvent('ASSIGNED', `Assigned to ${actor}`, actor));
  }

  patch.timeline = appendTimeline(incident.timeline as TimelineEntry[], ...events);

  await db.from('incidents').update(patch).eq('id', id);
  await logAudit({
    organizationId,
    category: 'INCIDENT',
    action: 'Acknowledged incident',
    actor,
    target: id,
  });
  return getIncident(organizationId, id);
}

export async function resolveIncident(
  organizationId: string,
  id: string,
  userId: string,
  actorFallback: string,
  data?: { rootCause?: string | null; notes?: string | null },
) {
  const db = getSupabaseAdmin();
  const { data: incident } = await db
    .from('incidents')
    .select('*')
    .eq('id', id)
    .eq('organization_id', organizationId)
    .single();
  if (!incident) throw new Error('Incident not found');
  if (incident.status === 'RESOLVED') throw new Error('Incident is already resolved');

  const actor = await getActorDisplayName(userId, actorFallback);
  const endedAt = new Date();
  const startedAt = new Date(incident.started_at);
  const timeline = appendTimeline(
    incident.timeline as TimelineEntry[],
    timelineEvent('RESOLVED', `Resolved by ${actor}`, actor),
  );

  await db
    .from('incidents')
    .update({
      status: 'RESOLVED',
      ended_at: endedAt.toISOString(),
      duration: Math.floor((endedAt.getTime() - startedAt.getTime()) / 1000),
      timeline,
      root_cause: data?.rootCause !== undefined ? data.rootCause : incident.root_cause,
      notes: data?.notes !== undefined ? data.notes : incident.notes,
    })
    .eq('id', id);

  await logAudit({
    organizationId,
    category: 'INCIDENT',
    action: 'Resolved incident',
    actor,
    target: id,
  });
  return getIncident(organizationId, id);
}

export async function updateIncident(
  organizationId: string,
  id: string,
  userId: string,
  actorFallback: string,
  data: {
    assignedUserId?: string | null;
    severity?: string;
    rootCause?: string | null;
    notes?: string | null;
    note?: string;
  },
) {
  const db = getSupabaseAdmin();
  const { data: incident } = await db
    .from('incidents')
    .select('*')
    .eq('id', id)
    .eq('organization_id', organizationId)
    .single();
  if (!incident) throw new Error('Incident not found');
  if (incident.status === 'RESOLVED') throw new Error('Cannot update a resolved incident');

  const actor = await getActorDisplayName(userId, actorFallback);
  const patch: Record<string, unknown> = {};
  let timeline = (incident.timeline as TimelineEntry[]) ?? [];

  if (data.severity !== undefined) patch.severity = data.severity;
  if (data.rootCause !== undefined) patch.root_cause = data.rootCause;
  if (data.notes !== undefined) patch.notes = data.notes;

  if (data.assignedUserId !== undefined) {
    patch.assigned_user_id = data.assignedUserId;
    if (data.assignedUserId) {
      const assignee = await getActorDisplayName(data.assignedUserId, 'Engineer');
      timeline = appendTimeline(
        timeline,
        timelineEvent('ASSIGNED', `Assigned to ${assignee}`, actor),
      );
      patch.timeline = timeline;
    }
  }

  if (data.note) {
    timeline = appendTimeline(
      timeline,
      timelineEvent('NOTE', data.note, actor),
    );
    patch.timeline = timeline;
  }

  if (Object.keys(patch).length === 0) return getIncident(organizationId, id);

  await db.from('incidents').update(patch).eq('id', id);
  await logAudit({
    organizationId,
    category: 'INCIDENT',
    action: 'Updated incident',
    actor,
    target: id,
  });
  return getIncident(organizationId, id);
}

export async function appendIncidentTimeline(
  organizationId: string,
  incidentId: string,
  ...events: TimelineEntry[]
) {
  const db = getSupabaseAdmin();
  const { data: incident } = await db
    .from('incidents')
    .select('timeline')
    .eq('id', incidentId)
    .eq('organization_id', organizationId)
    .single();
  if (!incident) return;
  const timeline = appendTimeline(incident.timeline as TimelineEntry[], ...events);
  await db.from('incidents').update({ timeline }).eq('id', incidentId);
}

export { deriveIncidentSeverity, timelineEvent, appendTimeline, type TimelineEntry };
