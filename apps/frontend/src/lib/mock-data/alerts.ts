/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { AlertChannel, AlertRule } from '@/lib/types';

const now = Date.now();
const mins = (n: number) => new Date(now - n * 60_000).toISOString();

export const initialAlertChannels: AlertChannel[] = [
  {
    id: 'ch-001',
    channel: 'EMAIL',
    name: 'Ops Email',
    config: { email: 'ops@acme.com' },
    isActive: true,
    rulesCount: 3,
    createdAt: mins(60 * 24 * 30),
  },
  {
    id: 'ch-002',
    channel: 'SLACK',
    name: 'Engineering Slack',
    config: { webhookUrl: 'https://hooks.slack.com/services/demo' },
    isActive: true,
    rulesCount: 2,
    createdAt: mins(60 * 24 * 20),
  },
  {
    id: 'ch-003',
    channel: 'WEBHOOK',
    name: 'PagerDuty Webhook',
    config: { url: 'https://events.pagerduty.com/demo' },
    isActive: true,
    rulesCount: 1,
    createdAt: mins(60 * 24 * 10),
  },
  {
    id: 'ch-004',
    channel: 'TELEGRAM',
    name: 'On-Call Telegram',
    config: { chatId: '-1001234567890' },
    isActive: false,
    rulesCount: 0,
    createdAt: mins(60 * 24 * 5),
  },
];

export const initialAlertRules: AlertRule[] = [
  {
    id: 'rule-001',
    trigger: 'MONITOR_DOWN',
    isActive: true,
    channel: { id: 'ch-001', name: 'Ops Email', channel: 'EMAIL' },
    monitor: null,
  },
  {
    id: 'rule-002',
    trigger: 'HIGH_LATENCY',
    isActive: true,
    channel: { id: 'ch-002', name: 'Engineering Slack', channel: 'SLACK' },
    monitor: { id: 'mon-003', name: 'Database Primary' },
  },
  {
    id: 'rule-003',
    trigger: 'MONITOR_RECOVERY',
    isActive: true,
    channel: { id: 'ch-001', name: 'Ops Email', channel: 'EMAIL' },
    monitor: null,
  },
  {
    id: 'rule-004',
    trigger: 'SSL_EXPIRY',
    isActive: true,
    channel: { id: 'ch-003', name: 'PagerDuty Webhook', channel: 'WEBHOOK' },
    monitor: { id: 'mon-007', name: 'SSL Certificate' },
  },
  {
    id: 'rule-005',
    trigger: 'PACKET_LOSS',
    isActive: false,
    channel: { id: 'ch-002', name: 'Engineering Slack', channel: 'SLACK' },
    monitor: { id: 'mon-004', name: 'VPN Gateway' },
  },
];

export const initialRecentAlerts = [
  { id: 'ra-1', monitor: 'VPN Gateway', type: 'Monitor Down', channel: 'Email', time: '2 min ago', status: 'sent' },
  { id: 'ra-2', monitor: 'Database Primary', type: 'High Latency', channel: 'Slack', time: '18 min ago', status: 'sent' },
  { id: 'ra-3', monitor: 'SSL Certificate', type: 'SSL Expiry', channel: 'Webhook', time: '1 hr ago', status: 'sent' },
  { id: 'ra-4', monitor: 'VPN Gateway', type: 'Monitor Down', channel: 'Webhook', time: '3 hr ago', status: 'failed' },
];
