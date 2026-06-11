/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { StatusPageComponent } from '@/lib/types';

const now = Date.now();
const days = (n: number) => new Date(now - n * 86_400_000).toISOString();

export const statusPageConfig = {
  organizationName: 'Acme Corporation',
  slug: 'acme-corp',
  overallStatus: 'DEGRADED' as const,
  overallUptime90d: 99.87,
  lastUpdated: new Date().toISOString(),
};

export const statusPageComponents: StatusPageComponent[] = [
  { id: 'sp-1', name: 'API Services', status: 'DOWN', uptime90d: 98.42 },
  { id: 'sp-2', name: 'Web Applications', status: 'UP', uptime90d: 99.91 },
  { id: 'sp-3', name: 'Database Layer', status: 'DEGRADED', uptime90d: 99.12 },
  { id: 'sp-4', name: 'DNS & CDN', status: 'UP', uptime90d: 99.97 },
  { id: 'sp-5', name: 'Internal Systems', status: 'DEGRADED', uptime90d: 97.80 },
  { id: 'sp-6', name: 'Security & SSL', status: 'UP', uptime90d: 100 },
];

export const statusPageUptimeHistory = Array.from({ length: 90 }, (_, i) => ({
  day: new Date(now - (89 - i) * 86_400_000).toISOString().slice(0, 10),
  uptime: 99.5 + Math.random() * 0.5 - (i === 45 ? 2 : 0),
}));

export const statusPageIncidents = [
  { id: 'spi-1', title: 'Payment API outage', status: 'INVESTIGATING', impact: 'Major', startedAt: days(0), components: ['API Services'] },
  { id: 'spi-2', title: 'Database latency elevated', status: 'MONITORING', impact: 'Minor', startedAt: days(0), components: ['Database Layer'] },
  { id: 'spi-3', title: 'DNS propagation delay', status: 'RESOLVED', impact: 'Minor', startedAt: days(2), resolvedAt: days(2), components: ['DNS & CDN'] },
  { id: 'spi-4', title: 'Scheduled maintenance — ERP patch', status: 'SCHEDULED', impact: 'Maintenance', startedAt: days(0), components: ['Internal Systems'] },
];

export const statusPageMaintenance = [
  { id: 'spm-1', title: 'PostgreSQL 16 upgrade', start: days(-1), end: days(0), status: 'Scheduled' },
  { id: 'spm-2', title: 'CDN edge node replacement', start: days(2), end: days(2), status: 'Scheduled' },
];
