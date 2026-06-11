/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { Monitor, MonitorCheck } from '@/lib/types';

const now = Date.now();
const mins = (n: number) => new Date(now - n * 60_000).toISOString();
const days = (n: number) => new Date(now - n * 86_400_000).toISOString();

export const initialMonitors: Monitor[] = [
  {
    id: 'mon-001', name: 'Google API', description: 'Google Maps & OAuth API gateway',
    type: 'HTTPS', target: 'https://maps.googleapis.com/maps/api/health',
    status: 'UP', tags: ['external', 'api', 'critical'], group: 'External Services',
    checkInterval: 60, timeout: 30, retryCount: 3, isActive: true, maintenanceMode: false,
    lastResponseTime: 142, lastCheckedAt: mins(1), uptimePercent: 99.98,
    createdAt: days(120), updatedAt: mins(1),
  },
  {
    id: 'mon-002', name: 'AWS Production Server', description: 'EC2 production load balancer health',
    type: 'HTTPS', target: 'https://prod.aws.acme-corp.com/health',
    status: 'UP', tags: ['aws', 'production', 'critical'], group: 'Cloud Infrastructure',
    checkInterval: 60, timeout: 30, retryCount: 3, isActive: true, maintenanceMode: false,
    lastResponseTime: 89, lastCheckedAt: mins(1), uptimePercent: 99.95,
    createdAt: days(180), updatedAt: mins(1),
  },
  {
    id: 'mon-003', name: 'Cloudflare DNS', description: 'Authoritative DNS resolution',
    type: 'DNS', target: 'acme-corp.com',
    status: 'UP', tags: ['dns', 'cloudflare'], group: 'Network',
    checkInterval: 300, timeout: 10, retryCount: 2, isActive: true, maintenanceMode: false,
    lastResponseTime: 18, lastCheckedAt: mins(3), uptimePercent: 99.99,
    createdAt: days(90), updatedAt: mins(3),
  },
  {
    id: 'mon-004', name: 'Payment API', description: 'Stripe payment gateway integration',
    type: 'API', target: 'https://api.acme-corp.com/v1/payments/health',
    status: 'DOWN', tags: ['payments', 'critical', 'revenue'], group: 'Production',
    checkInterval: 30, timeout: 15, retryCount: 3, isActive: true, maintenanceMode: false,
    lastResponseTime: null, lastCheckedAt: mins(2), uptimePercent: 98.42,
    createdAt: days(200), updatedAt: mins(2),
  },
  {
    id: 'mon-005', name: 'Customer Portal', description: 'Self-service customer dashboard',
    type: 'HTTPS', target: 'https://portal.acme-corp.com',
    status: 'UP', tags: ['web', 'customer-facing'], group: 'Production',
    checkInterval: 120, timeout: 30, retryCount: 2, isActive: true, maintenanceMode: false,
    lastResponseTime: 234, lastCheckedAt: mins(2), uptimePercent: 99.87,
    createdAt: days(150), updatedAt: mins(2),
  },
  {
    id: 'mon-006', name: 'Internal ERP', description: 'SAP ERP system availability',
    type: 'HTTP', target: 'http://erp.internal.acme-corp.com/status',
    status: 'DEGRADED', tags: ['internal', 'erp'], group: 'Internal Systems',
    checkInterval: 300, timeout: 45, retryCount: 2, isActive: true, maintenanceMode: false,
    lastResponseTime: 1240, lastCheckedAt: mins(1), uptimePercent: 97.8,
    createdAt: days(365), updatedAt: mins(1),
  },
  {
    id: 'mon-007', name: 'PostgreSQL Cluster', description: 'Primary database cluster (read/write)',
    type: 'TCP', target: 'db-primary.internal:5432',
    status: 'DEGRADED', tags: ['database', 'critical'], group: 'Data Layer',
    checkInterval: 60, timeout: 15, retryCount: 3, isActive: true, maintenanceMode: false,
    lastResponseTime: 890, lastCheckedAt: mins(1), uptimePercent: 99.12,
    createdAt: days(400), updatedAt: mins(1),
  },
  {
    id: 'mon-008', name: 'Redis Cache', description: 'Session & cache layer',
    type: 'TCP', target: 'redis-cluster.internal:6379',
    status: 'UP', tags: ['cache', 'redis'], group: 'Data Layer',
    checkInterval: 60, timeout: 10, retryCount: 2, isActive: true, maintenanceMode: false,
    lastResponseTime: 4, lastCheckedAt: mins(1), uptimePercent: 99.99,
    createdAt: days(300), updatedAt: mins(1),
  },
  {
    id: 'mon-009', name: 'SSL Certificate — api.acme-corp.com', description: 'TLS certificate expiry monitoring',
    type: 'SSL', target: 'api.acme-corp.com',
    status: 'UP', tags: ['security', 'ssl'], group: 'Security',
    checkInterval: 86400, timeout: 30, retryCount: 1, isActive: true, maintenanceMode: false,
    lastResponseTime: 320, lastCheckedAt: mins(60), uptimePercent: 100,
    createdAt: days(60), updatedAt: mins(60),
  },
  {
    id: 'mon-010', name: 'SSL Certificate — portal.acme-corp.com', description: 'Portal TLS certificate',
    type: 'SSL', target: 'portal.acme-corp.com',
    status: 'DEGRADED', tags: ['security', 'ssl', 'warning'], group: 'Security',
    checkInterval: 86400, timeout: 30, retryCount: 1, isActive: true, maintenanceMode: false,
    lastResponseTime: 280, lastCheckedAt: mins(120), uptimePercent: 100,
    createdAt: days(45), updatedAt: mins(120),
  },
  {
    id: 'mon-011', name: 'VPN Gateway', description: 'Corporate VPN endpoint',
    type: 'PING', target: '10.0.1.1',
    status: 'UP', tags: ['network', 'vpn'], group: 'Network',
    checkInterval: 60, timeout: 10, retryCount: 3, isActive: true, maintenanceMode: false,
    lastResponseTime: 12, lastCheckedAt: mins(1), uptimePercent: 99.5,
    createdAt: days(250), updatedAt: mins(1),
  },
  {
    id: 'mon-012', name: 'CDN Edge — Global', description: 'Global CDN edge node health',
    type: 'HTTPS', target: 'https://cdn.acme-corp.com/health',
    status: 'UP', tags: ['cdn', 'global'], group: 'Cloud Infrastructure',
    checkInterval: 300, timeout: 30, retryCount: 2, isActive: true, maintenanceMode: false,
    lastResponseTime: 45, lastCheckedAt: mins(5), uptimePercent: 99.94,
    createdAt: days(100), updatedAt: mins(5),
  },
];

function genChecks(monitorId: string, baseMs: number, count: number, statusMix: string[]): MonitorCheck[] {
  return Array.from({ length: count }, (_, i) => ({
    id: `chk-${monitorId}-${i}`,
    status: statusMix[i % statusMix.length],
    responseTime: statusMix[i % statusMix.length] === 'DOWN' ? null : baseMs + Math.floor(Math.random() * 80 - 40),
    createdAt: mins(i + 1),
  }));
}

export const initialMonitorChecks: Record<string, MonitorCheck[]> = {
  'mon-001': genChecks('mon-001', 142, 12, ['UP', 'UP', 'UP', 'DEGRADED', 'UP']),
  'mon-002': genChecks('mon-002', 89, 10, ['UP', 'UP', 'UP', 'UP']),
  'mon-003': genChecks('mon-003', 18, 8, ['UP', 'UP', 'UP']),
  'mon-004': genChecks('mon-004', 0, 10, ['DOWN', 'DOWN', 'DOWN', 'UP', 'DOWN']),
  'mon-005': genChecks('mon-005', 234, 10, ['UP', 'UP', 'UP', 'UP']),
  'mon-006': genChecks('mon-006', 1240, 10, ['DEGRADED', 'DEGRADED', 'UP', 'DEGRADED']),
  'mon-007': genChecks('mon-007', 890, 12, ['DEGRADED', 'DEGRADED', 'UP', 'DEGRADED', 'UP']),
  'mon-008': genChecks('mon-008', 4, 8, ['UP', 'UP', 'UP', 'UP']),
  'mon-009': genChecks('mon-009', 320, 5, ['UP', 'UP']),
  'mon-010': genChecks('mon-010', 280, 5, ['DEGRADED', 'UP']),
  'mon-011': genChecks('mon-011', 12, 8, ['UP', 'UP', 'UP']),
  'mon-012': genChecks('mon-012', 45, 8, ['UP', 'UP', 'UP']),
};

export const initialMonitorAlertHistory: Record<string, { id: string; trigger: string; status: string; createdAt: string }[]> = {
  'mon-004': [
    { id: 'al-004-1', trigger: 'MONITOR_DOWN', status: 'SENT', createdAt: mins(2) },
    { id: 'al-004-2', trigger: 'MONITOR_DOWN', status: 'SENT', createdAt: mins(15) },
    { id: 'al-004-3', trigger: 'MONITOR_DOWN', status: 'FAILED', createdAt: mins(30) },
  ],
  'mon-007': [
    { id: 'al-007-1', trigger: 'HIGH_LATENCY', status: 'SENT', createdAt: mins(20) },
  ],
  'mon-006': [
    { id: 'al-006-1', trigger: 'HIGH_LATENCY', status: 'SENT', createdAt: mins(45) },
  ],
  'mon-010': [
    { id: 'al-010-1', trigger: 'SSL_EXPIRY', status: 'SENT', createdAt: mins(120) },
  ],
};
