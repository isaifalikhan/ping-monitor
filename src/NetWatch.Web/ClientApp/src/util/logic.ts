// Pure UI logic shared by the pages and covered by the vitest suite (no React, no DOM).
import type { Alert, NetInterface, StatusCounts } from '../api/types';
import { severityRank } from '../api/types';

/** CSS classes of one port in the switch port grid. */
export function portClass(i: Pick<NetInterface, 'adminStatus' | 'operStatus' | 'isImportant' | 'inUtilPct' | 'outUtilPct' | 'duplex' | 'flapCount24h'>): string {
  const util = Math.max(i.inUtilPct ?? 0, i.outUtilPct ?? 0);
  const cls = ['port'];
  if (i.adminStatus !== 1) cls.push('admin-down');
  else if (i.operStatus === 1) cls.push(util >= 80 ? 'hot' : 'up');
  else cls.push(i.isImportant ? 'problem' : 'down');
  if (i.isImportant) cls.push('important');
  if (i.operStatus === 1 && i.duplex === 2) cls.push('half-duplex');
  if ((i.flapCount24h ?? 0) >= 4) cls.push('flappy');
  return cls.join(' ');
}

/** Open alerts for lists: active before acknowledged, then by severity (worst first), then newest first. */
export function sortOpenAlerts<T extends Pick<Alert, 'state' | 'severity' | 'firstSeenUtc'>>(alerts: readonly T[]): T[] {
  return alerts
    .filter((a) => a.state === 'Active' || a.state === 'Acknowledged')
    .slice()
    .sort(
      (a, b) =>
        (a.state === b.state ? 0 : a.state === 'Active' ? -1 : 1) || severityRank[b.severity] - severityRank[a.severity] || b.firstSeenUtc.localeCompare(a.firstSeenUtc),
    );
}

/** Colour state of a NOC group tile. */
export function groupState(c: Pick<StatusCounts, 'down' | 'warning'>): 'down' | 'warning' | 'up' {
  return c.down > 0 ? 'down' : c.warning > 0 ? 'warning' : 'up';
}

/** Filters of the DC security event viewer (also the shape of a saved search). */
export interface EventQuery {
  deviceId: string;
  preset: string;
  eventIds: string;
  user: string;
  source: string;
  search: string;
  range: string;
}

export interface EventPreset {
  key: string;
  label: string;
  eventIds?: string;
  failedOnly?: boolean;
}

/** Query-string parameters for /api/dc/security-events from the viewer's filters. */
export function eventQueryParams(query: EventQuery, presets: readonly EventPreset[], ranges: readonly { key: string; hours?: number }[], page: number, nowMs: number) {
  const preset = presets.find((p) => p.key === query.preset);
  const hours = ranges.find((r) => r.key === query.range)?.hours;
  return {
    deviceId: query.deviceId || undefined,
    eventIds: query.eventIds.trim() || preset?.eventIds,
    failedOnly: preset?.failedOnly || undefined,
    user: query.user.trim() || undefined,
    source: query.source.trim() || undefined,
    search: query.search.trim() || undefined,
    from: hours ? new Date(nowMs - hours * 3600_000).toISOString() : undefined,
    page,
    pageSize: 500,
  };
}
