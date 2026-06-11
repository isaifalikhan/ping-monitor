/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { AlertChannel, AlertRule, AlertDelivery } from '@/lib/types';

const now = Date.now();
const mins = (n: number) => new Date(now - n * 60_000).toISOString();
const hours = (n: number) => new Date(now - n * 3_600_000).toISOString();
const days = (n: number) => new Date(now - n * 86_400_000).toISOString();

export const initialAlertChannels: AlertChannel[] = [
  { id: 'ch-001', channel: 'EMAIL', name: 'Ops Team Email', config: { email: 'ops@acme-corp.com' }, isActive: true, rulesCount: 4, successRate: 99.2, deliveryCount: 1247, createdAt: days(120) },
  { id: 'ch-002', channel: 'SLACK', name: '#incidents-alerts', config: { webhookUrl: 'https://hooks.slack.com/services/demo' }, isActive: true, rulesCount: 3, successRate: 98.7, deliveryCount: 892, createdAt: days(90) },
  { id: 'ch-003', channel: 'TEAMS', name: 'SRE War Room', config: { webhookUrl: 'https://outlook.office.com/webhook/demo' }, isActive: true, rulesCount: 2, successRate: 97.5, deliveryCount: 456, createdAt: days(60) },
  { id: 'ch-004', channel: 'TELEGRAM', name: 'On-Call Bot', config: { chatId: '-1001234567890' }, isActive: true, rulesCount: 2, successRate: 99.8, deliveryCount: 234, createdAt: days(45) },
  { id: 'ch-005', channel: 'WHATSAPP', name: 'Executive Alerts', config: { phone: '+1-555-0100' }, isActive: true, rulesCount: 1, successRate: 96.0, deliveryCount: 89, createdAt: days(30) },
  { id: 'ch-006', channel: 'WEBHOOK', name: 'PagerDuty Integration', config: { url: 'https://events.pagerduty.com/v2/enqueue' }, isActive: true, rulesCount: 3, successRate: 99.9, deliveryCount: 567, createdAt: days(100) },
];

export const initialAlertRules: AlertRule[] = [
  { id: 'rule-001', trigger: 'MONITOR_DOWN', isActive: true, channel: { id: 'ch-001', name: 'Ops Team Email', channel: 'EMAIL' }, monitor: null },
  { id: 'rule-002', trigger: 'MONITOR_DOWN', isActive: true, channel: { id: 'ch-002', name: '#incidents-alerts', channel: 'SLACK' }, monitor: null },
  { id: 'rule-003', trigger: 'HIGH_LATENCY', isActive: true, channel: { id: 'ch-003', name: 'SRE War Room', channel: 'TEAMS' }, monitor: { id: 'mon-007', name: 'PostgreSQL Cluster' } },
  { id: 'rule-004', trigger: 'SSL_EXPIRY', isActive: true, channel: { id: 'ch-001', name: 'Ops Team Email', channel: 'EMAIL' }, monitor: { id: 'mon-010', name: 'SSL Certificate — portal.acme-corp.com' } },
  { id: 'rule-005', trigger: 'MONITOR_DOWN', isActive: true, channel: { id: 'ch-006', name: 'PagerDuty Integration', channel: 'WEBHOOK' }, monitor: { id: 'mon-004', name: 'Payment API' } },
  { id: 'rule-006', trigger: 'MONITOR_RECOVERY', isActive: true, channel: { id: 'ch-002', name: '#incidents-alerts', channel: 'SLACK' }, monitor: null },
  { id: 'rule-007', trigger: 'DNS_FAILURE', isActive: true, channel: { id: 'ch-004', name: 'On-Call Bot', channel: 'TELEGRAM' }, monitor: { id: 'mon-003', name: 'Cloudflare DNS' } },
];

export const initialAlertDeliveries: AlertDelivery[] = [
  { id: 'del-001', channel: 'EMAIL', channelName: 'Ops Team Email', monitor: 'Payment API', trigger: 'MONITOR_DOWN', status: 'SENT', message: 'Payment API is DOWN', createdAt: mins(2) },
  { id: 'del-002', channel: 'SLACK', channelName: '#incidents-alerts', monitor: 'Payment API', trigger: 'MONITOR_DOWN', status: 'SENT', createdAt: mins(2) },
  { id: 'del-003', channel: 'WEBHOOK', channelName: 'PagerDuty Integration', monitor: 'Payment API', trigger: 'MONITOR_DOWN', status: 'SENT', createdAt: mins(2) },
  { id: 'del-004', channel: 'TEAMS', channelName: 'SRE War Room', monitor: 'PostgreSQL Cluster', trigger: 'HIGH_LATENCY', status: 'SENT', createdAt: mins(18) },
  { id: 'del-005', channel: 'EMAIL', channelName: 'Ops Team Email', monitor: 'PostgreSQL Cluster', trigger: 'HIGH_LATENCY', status: 'SENT', createdAt: mins(20) },
  { id: 'del-006', channel: 'TELEGRAM', channelName: 'On-Call Bot', monitor: 'SSL Certificate — portal', trigger: 'SSL_EXPIRY', status: 'SENT', createdAt: hours(4) },
  { id: 'del-007', channel: 'WHATSAPP', channelName: 'Executive Alerts', monitor: 'Payment API', trigger: 'MONITOR_DOWN', status: 'FAILED', message: 'Delivery timeout after 30s', createdAt: mins(5) },
  { id: 'del-008', channel: 'SLACK', channelName: '#incidents-alerts', monitor: 'Internal ERP', trigger: 'HIGH_LATENCY', status: 'SENT', createdAt: mins(45) },
  { id: 'del-009', channel: 'EMAIL', channelName: 'Ops Team Email', monitor: 'Cloudflare DNS', trigger: 'DNS_FAILURE', status: 'SENT', createdAt: hours(48) },
  { id: 'del-010', channel: 'WEBHOOK', channelName: 'PagerDuty Integration', monitor: 'Payment API', trigger: 'MONITOR_RECOVERY', status: 'SENT', createdAt: hours(71) },
];

export const initialRecentAlerts = [
  { id: 'ra-1', monitor: 'Payment API', type: 'Monitor Down', channel: 'Email', time: '2 min ago', status: 'sent' },
  { id: 'ra-2', monitor: 'Payment API', type: 'Monitor Down', channel: 'Slack', time: '2 min ago', status: 'sent' },
  { id: 'ra-3', monitor: 'PostgreSQL Cluster', type: 'High Latency', channel: 'Teams', time: '18 min ago', status: 'sent' },
  { id: 'ra-4', monitor: 'SSL Certificate — portal', type: 'SSL Expiry', channel: 'Telegram', time: '4 hr ago', status: 'sent' },
  { id: 'ra-5', monitor: 'Payment API', type: 'Monitor Down', channel: 'WhatsApp', time: '5 min ago', status: 'failed' },
  { id: 'ra-6', monitor: 'Internal ERP', type: 'High Latency', channel: 'Slack', time: '45 min ago', status: 'sent' },
];

export const alertStats = {
  totalSent: 3485,
  totalFailed: 42,
  successRate: 98.8,
  last24h: 47,
  avgDeliveryMs: 1240,
};
