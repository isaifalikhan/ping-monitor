/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { MaintenanceWindow } from '@/lib/types';

const now = Date.now();
const hours = (n: number) => new Date(now + n * 3_600_000).toISOString();
const hoursAgo = (n: number) => new Date(now - n * 3_600_000).toISOString();

export const initialMaintenanceWindows: MaintenanceWindow[] = [
  {
    id: 'mw-001',
    title: 'Database upgrade',
    monitor: { id: 'mon-003', name: 'Database Primary' },
    startTime: hours(24),
    endTime: hours(26),
    reason: 'PostgreSQL 16 migration',
    pauseAlerts: true,
    createdAt: hoursAgo(48),
  },
  {
    id: 'mw-002',
    title: 'CDN maintenance',
    monitor: { id: 'mon-005', name: 'CDN Edge' },
    startTime: hours(72),
    endTime: hours(74),
    reason: 'Edge node replacement',
    pauseAlerts: true,
    createdAt: hoursAgo(24),
  },
];
