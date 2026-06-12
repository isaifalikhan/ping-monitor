/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { Incident } from '@/lib/types';

const now = Date.now();
const mins = (n: number) => new Date(now - n * 60_000).toISOString();
const hours = (n: number) => new Date(now - n * 3_600_000).toISOString();

export const initialIncidents: Incident[] = [
  {
    id: 'inc-001',
    title: 'Payment API timeout — gateway unreachable',
    monitor: { id: 'mon-004', name: 'Payment API', target: 'https://api.acme-corp.com/v1/payments/health' },
    status: 'OPEN', severity: 'CRITICAL',
    startedAt: mins(45), endedAt: null, duration: 45 * 60,
    rootCause: 'Under investigation — Stripe webhook endpoint returning 503',
    notes: 'PagerDuty escalated to on-call. Payment team engaged.',
    assignedUser: { id: 'usr-003', firstName: 'Marcus', lastName: 'Rivera', email: 'marcus@acme-corp.com' },
    timeline: [
      { id: 't1', type: 'DETECTED', message: 'Monitor detected DOWN — 3 consecutive failures', createdAt: mins(45) },
      { id: 't2', type: 'ESCALATED', message: 'Auto-escalated to PagerDuty on-call', user: 'System', createdAt: mins(44) },
      { id: 't3', type: 'NOTE', message: 'Stripe status page shows partial outage in US-East', user: 'Marcus Rivera', createdAt: mins(30) },
    ],
  },
  {
    id: 'inc-002',
    title: 'PostgreSQL cluster latency spike',
    monitor: { id: 'mon-007', name: 'PostgreSQL Cluster', target: 'db-primary.internal:5432' },
    status: 'ACKNOWLEDGED', severity: 'HIGH',
    startedAt: mins(90), endedAt: null, duration: 90 * 60,
    rootCause: 'Connection pool exhaustion during batch ETL job',
    notes: 'DBA team investigating query performance. ETL paused.',
    assignedUser: { id: 'usr-002', firstName: 'Sarah', lastName: 'Chen', email: 'sarah@acme-corp.com' },
    timeline: [
      { id: 't4', type: 'DETECTED', message: 'Response time exceeded 500ms threshold (890ms)', createdAt: mins(90) },
      { id: 't5', type: 'ACKNOWLEDGED', message: 'Incident acknowledged by Sarah Chen', user: 'Sarah Chen', createdAt: mins(75) },
      { id: 't6', type: 'NOTE', message: 'Identified long-running analytics query from BI team', user: 'Sarah Chen', createdAt: mins(60) },
    ],
  },
  {
    id: 'inc-003',
    title: 'SSL certificate expiry warning — portal.acme-corp.com',
    monitor: { id: 'mon-010', name: 'SSL Certificate — portal.acme-corp.com', target: 'portal.acme-corp.com' },
    status: 'ACKNOWLEDGED', severity: 'MEDIUM',
    startedAt: hours(4), endedAt: null, duration: 4 * 3600,
    rootCause: 'Certificate expires in 14 days — renewal pending',
    notes: 'Security team scheduled renewal for next maintenance window.',
    assignedUser: { id: 'usr-004', firstName: 'James', lastName: 'Okonkwo', email: 'james@acme-corp.com' },
    timeline: [
      { id: 't7', type: 'DETECTED', message: 'SSL certificate expires in 14 days', createdAt: hours(4) },
      { id: 't8', type: 'ACKNOWLEDGED', message: 'Acknowledged by James Okonkwo', user: 'James Okonkwo', createdAt: hours(3) },
    ],
  },
  {
    id: 'inc-004',
    title: 'Internal ERP degraded performance',
    monitor: { id: 'mon-006', name: 'Internal ERP', target: 'http://erp.internal.acme-corp.com/status' },
    status: 'OPEN', severity: 'MEDIUM',
    startedAt: mins(120), endedAt: null, duration: 120 * 60,
    rootCause: 'SAP batch job causing connection pool saturation on app tier',
    notes: 'ERP team investigating batch window overlap with peak hours.',
    assignedUser: { id: 'usr-004', firstName: 'James', lastName: 'Okonkwo', email: 'james@acme-corp.com' },
    timeline: [
      { id: 't9', type: 'DETECTED', message: 'Response time degraded — 1240ms (threshold: 500ms)', createdAt: mins(120) },
      { id: 't9b', type: 'NOTE', message: 'Correlated with nightly ETL job start', user: 'James Okonkwo', createdAt: mins(90) },
    ],
  },
  {
    id: 'inc-005',
    title: 'DNS resolution failure — Cloudflare',
    monitor: { id: 'mon-003', name: 'Cloudflare DNS', target: 'acme-corp.com' },
    status: 'RESOLVED', severity: 'HIGH',
    startedAt: hours(48), endedAt: hours(47), duration: 22 * 60,
    rootCause: 'Cloudflare DNS propagation delay after record update',
    notes: 'Resolved after DNS TTL expired. No customer impact.',
    assignedUser: { id: 'usr-002', firstName: 'Sarah', lastName: 'Chen' },
    timeline: [
      { id: 't10', type: 'DETECTED', message: 'DNS lookup failed for acme-corp.com', createdAt: hours(48) },
      { id: 't11', type: 'ACKNOWLEDGED', message: 'Acknowledged by Sarah Chen', user: 'Sarah Chen', createdAt: hours(47.5) },
      { id: 't12', type: 'RESOLVED', message: 'DNS propagation complete — monitor recovered', user: 'Sarah Chen', createdAt: hours(47) },
    ],
  },
  {
    id: 'inc-006',
    title: 'Payment gateway outage — partial recovery',
    monitor: { id: 'mon-004', name: 'Payment API', target: 'https://api.acme-corp.com/v1/payments/health' },
    status: 'RESOLVED', severity: 'CRITICAL',
    startedAt: hours(72), endedAt: hours(71), duration: 38 * 60,
    rootCause: 'Stripe API regional outage — resolved by provider',
    notes: 'Post-mortem scheduled for Friday.',
    assignedUser: { id: 'usr-003', firstName: 'Marcus', lastName: 'Rivera' },
    timeline: [
      { id: 't13', type: 'DETECTED', message: 'Payment API returned HTTP 503', createdAt: hours(72) },
      { id: 't14', type: 'ACKNOWLEDGED', message: 'Acknowledged by Marcus Rivera', user: 'Marcus Rivera', createdAt: hours(71.8) },
      { id: 't15', type: 'RESOLVED', message: 'Stripe API recovered — all checks passing', user: 'Marcus Rivera', createdAt: hours(71) },
    ],
  },
];
