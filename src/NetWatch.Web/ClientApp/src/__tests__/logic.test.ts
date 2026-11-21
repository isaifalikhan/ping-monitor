import { describe, expect, it } from 'vitest';
import { qs } from '../api/client';
import type { Alert } from '../api/types';
import { eventQueryParams, groupState, portClass, sortOpenAlerts, type EventQuery } from '../util/logic';

describe('port grid', () => {
  const base = { adminStatus: 1, operStatus: 1, isImportant: false, inUtilPct: 10, outUtilPct: 5, duplex: 3, flapCount24h: 0 };

  it('colours ports by state, load and importance', () => {
    expect(portClass(base)).toBe('port up');
    expect(portClass({ ...base, inUtilPct: 85 })).toBe('port hot');
    expect(portClass({ ...base, operStatus: 2 })).toBe('port down');
    expect(portClass({ ...base, operStatus: 2, isImportant: true })).toBe('port problem important');
    expect(portClass({ ...base, adminStatus: 2, operStatus: 2 })).toBe('port admin-down');
  });

  it('marks half duplex only while up, and frequent flapping', () => {
    expect(portClass({ ...base, duplex: 2 })).toContain('half-duplex');
    expect(portClass({ ...base, duplex: 2, operStatus: 2 })).not.toContain('half-duplex');
    expect(portClass({ ...base, flapCount24h: 4 })).toContain('flappy');
    expect(portClass({ ...base, flapCount24h: 3 })).not.toContain('flappy');
  });
});

describe('alert list ordering', () => {
  const a = (id: number, state: Alert['state'], severity: Alert['severity'], firstSeenUtc: string) => ({ id, state, severity, firstSeenUtc });

  it('puts active before acknowledged, worst first, newest first, and drops closed alerts', () => {
    const sorted = sortOpenAlerts([
      a(1, 'Acknowledged', 'Critical', '2026-11-21T10:00:00Z'),
      a(2, 'Active', 'Warning', '2026-11-21T09:00:00Z'),
      a(3, 'Active', 'Critical', '2026-11-21T08:00:00Z'),
      a(4, 'Active', 'Critical', '2026-11-21T11:00:00Z'),
      a(5, 'Resolved', 'Critical', '2026-11-21T12:00:00Z'),
      a(6, 'Closed', 'Major', '2026-11-21T12:00:00Z'),
    ]);
    expect(sorted.map((x) => x.id)).toEqual([4, 3, 2, 1]);
  });
});

describe('NOC group tiles', () => {
  it('turns red on any down device, amber on warnings', () => {
    expect(groupState({ down: 1, warning: 5 })).toBe('down');
    expect(groupState({ down: 0, warning: 1 })).toBe('warning');
    expect(groupState({ down: 0, warning: 0 })).toBe('up');
  });
});

describe('DC security event filters', () => {
  const presets = [
    { key: '', label: 'All' },
    { key: 'failed', label: 'Failed logons', eventIds: '4625,4771,4776', failedOnly: true },
    { key: 'lockouts', label: 'Lockouts', eventIds: '4740' },
  ];
  const ranges = [{ key: '24h', hours: 24 }, { key: 'all' }];
  const empty: EventQuery = { deviceId: '', preset: '', eventIds: '', user: '', source: '', search: '', range: '24h' };
  const now = Date.parse('2026-11-21T12:00:00Z');

  it('sends only the filters that are set, trimmed', () => {
    const p = eventQueryParams({ ...empty, user: '  bilal.ahmed ', deviceId: '7' }, presets, ranges, 2, now);
    expect(p).toMatchObject({ deviceId: '7', user: 'bilal.ahmed', page: 2, pageSize: 500, from: '2026-11-20T12:00:00.000Z' });
    expect(p.source).toBeUndefined();
    expect(p.eventIds).toBeUndefined();
    expect(qs(p)).not.toContain('source');
  });

  it('applies the preset unless explicit event IDs are typed', () => {
    expect(eventQueryParams({ ...empty, preset: 'failed' }, presets, ranges, 1, now)).toMatchObject({ eventIds: '4625,4771,4776', failedOnly: true });
    expect(eventQueryParams({ ...empty, preset: 'failed', eventIds: '4740 ' }, presets, ranges, 1, now).eventIds).toBe('4740');
    expect(eventQueryParams({ ...empty, range: 'all' }, presets, ranges, 1, now).from).toBeUndefined();
  });
});
