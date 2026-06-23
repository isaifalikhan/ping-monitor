export const INCIDENT_SEVERITIES = ['LOW', 'MEDIUM', 'HIGH', 'CRITICAL'] as const;

export type IncidentSeverity = (typeof INCIDENT_SEVERITIES)[number];

export type IncidentTimelineType =
  | 'DETECTED'
  | 'ACKNOWLEDGED'
  | 'ASSIGNED'
  | 'ALERT_SENT'
  | 'NOTE'
  | 'ESCALATED'
  | 'RESOLVED';

export type TimelineEntry = {
  type: IncidentTimelineType | string;
  message: string;
  user?: string;
  createdAt: string;
};

export function timelineEvent(
  type: IncidentTimelineType,
  message: string,
  user?: string,
): TimelineEntry {
  return { type, message, user, createdAt: new Date().toISOString() };
}

export function appendTimeline(
  existing: TimelineEntry[] | null | undefined,
  ...events: TimelineEntry[]
): TimelineEntry[] {
  return [...(existing ?? []), ...events];
}

/** Map monitor criticality + check status to incident severity. */
export function deriveIncidentSeverity(
  monitorCriticality: string | null | undefined,
  checkStatus: string,
): IncidentSeverity {
  const criticality = monitorCriticality ?? 'MEDIUM';
  if (checkStatus === 'DOWN') {
    if (criticality === 'LOW') return 'MEDIUM';
    if (criticality === 'MEDIUM') return 'HIGH';
    return 'CRITICAL';
  }
  // DEGRADED
  if (criticality === 'CRITICAL' || criticality === 'HIGH') return 'HIGH';
  if (criticality === 'MEDIUM') return 'MEDIUM';
  return 'LOW';
}

export function mapTimelineForApi(
  timeline: TimelineEntry[] | null | undefined,
  fallbackDate: string,
) {
  return (timeline ?? []).map((t, idx) => ({
    id: String(idx),
    type: t.type,
    message: t.message,
    user: t.user,
    createdAt: t.createdAt ?? fallbackDate,
  }));
}
