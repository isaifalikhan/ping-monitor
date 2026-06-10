/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { Incident } from '@/lib/types';

const now = Date.now();
const mins = (n: number) => new Date(now - n * 60_000).toISOString();

export const initialIncidents: Incident[] = [
  {
    id: 'inc-001',
    monitor: { id: 'mon-004', name: 'VPN Gateway', target: '10.0.1.1' },
    status: 'OPEN',
    severity: 'CRITICAL',
    startedAt: mins(45),
    endedAt: null,
    duration: 45 * 60,
    rootCause: null,
    notes: null,
    assignedUser: null,
  },
  {
    id: 'inc-002',
    monitor: { id: 'mon-003', name: 'Database Primary', target: 'db-primary.internal:5432' },
    status: 'ACKNOWLEDGED',
    severity: 'HIGH',
    startedAt: mins(90),
    endedAt: null,
    duration: 90 * 60,
    rootCause: 'Connection pool exhaustion',
    notes: 'Investigating query performance',
    assignedUser: { id: 'usr-002', firstName: 'Sarah', lastName: 'Chen', email: 'sarah@acme.com' },
  },
  {
    id: 'inc-003',
    monitor: { id: 'mon-001', name: 'Production API', target: 'https://api.acme.com/health' },
    status: 'RESOLVED',
    severity: 'MEDIUM',
    startedAt: mins(180),
    endedAt: mins(172),
    duration: 8 * 60,
    rootCause: 'Deployment rollback required',
    notes: 'Resolved after rollback to v2.4.1',
    assignedUser: { id: 'usr-003', firstName: 'James', lastName: 'Wilson', email: 'james@acme.com' },
  },
  {
    id: 'inc-004',
    monitor: { id: 'mon-006', name: 'Mail Server', target: 'mail.acme.com:587' },
    status: 'RESOLVED',
    severity: 'LOW',
    startedAt: mins(360),
    endedAt: mins(352),
    duration: 8 * 60,
    rootCause: 'SMTP relay timeout',
    notes: null,
    assignedUser: { id: 'usr-002', firstName: 'Sarah', lastName: 'Chen' },
  },
  {
    id: 'inc-005',
    monitor: { id: 'mon-004', name: 'VPN Gateway', target: '10.0.1.1' },
    status: 'RESOLVED',
    severity: 'HIGH',
    startedAt: mins(1440),
    endedAt: mins(1430),
    duration: 10 * 60,
    rootCause: 'Firewall rule change',
    notes: 'Reverted firewall policy',
    assignedUser: null,
  },
];
