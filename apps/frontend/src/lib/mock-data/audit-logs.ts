/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { AuditLogEntry } from '@/lib/types';

const now = Date.now();
const mins = (n: number) => new Date(now - n * 60_000).toISOString();
const hours = (n: number) => new Date(now - n * 3_600_000).toISOString();
const days = (n: number) => new Date(now - n * 86_400_000).toISOString();

export const initialAuditLogs: AuditLogEntry[] = [
  { id: 'aud-001', category: 'INCIDENT', action: 'Acknowledged incident INC-002', actor: 'Sarah Chen', target: 'PostgreSQL cluster latency', ipAddress: '10.0.1.45', createdAt: mins(75) },
  { id: 'aud-002', category: 'ALERT', action: 'Created alert rule — MONITOR_DOWN → PagerDuty', actor: 'Alex Morgan', target: 'Payment API', ipAddress: '10.0.1.12', createdAt: hours(2) },
  { id: 'aud-003', category: 'MONITOR', action: 'Created monitor "CDN Edge — Global"', actor: 'Marcus Rivera', target: 'mon-012', ipAddress: '10.0.1.88', createdAt: days(3) },
  { id: 'aud-004', category: 'SECURITY', action: 'Failed login attempt (3x)', actor: 'unknown@external.com', ipAddress: '203.0.113.42', createdAt: hours(6) },
  { id: 'aud-005', category: 'USER', action: 'Invited team member emily.park@acme-corp.com', actor: 'Alex Morgan', target: 'VIEWER role', ipAddress: '10.0.1.12', createdAt: days(5) },
  { id: 'aud-006', category: 'SETTINGS', action: 'Updated latency threshold to 500ms', actor: 'Alex Morgan', target: 'Organization settings', ipAddress: '10.0.1.12', createdAt: days(7) },
  { id: 'aud-007', category: 'MONITOR', action: 'Paused monitor "DNS Resolver"', actor: 'Sarah Chen', target: 'mon-legacy', ipAddress: '10.0.1.45', createdAt: days(10) },
  { id: 'aud-008', category: 'ALERT', action: 'Test alert sent via Slack', actor: 'Marcus Rivera', target: '#incidents-alerts', ipAddress: '10.0.1.88', createdAt: hours(12) },
  { id: 'aud-009', category: 'INCIDENT', action: 'Resolved incident INC-005', actor: 'Sarah Chen', target: 'DNS resolution failure', ipAddress: '10.0.1.45', createdAt: hours(47) },
  { id: 'aud-010', category: 'SECURITY', action: 'Password changed', actor: 'James Okonkwo', ipAddress: '10.0.1.67', createdAt: days(14) },
  { id: 'aud-011', category: 'USER', action: 'Changed role for David Kim → Network Engineer', actor: 'Alex Morgan', target: 'usr-006', ipAddress: '10.0.1.12', createdAt: days(20) },
  { id: 'aud-012', category: 'MONITOR', action: 'Updated monitor "Payment API" check interval', actor: 'Marcus Rivera', target: 'mon-004', ipAddress: '10.0.1.88', createdAt: days(25) },
];
