/** Production-ready alert triggers */
export const ALERT_TRIGGERS = [
  'MONITOR_DOWN',
  'MONITOR_RECOVERY',
  'HIGH_LATENCY',
  'SSL_EXPIRY',
  'DNS_FAILURE',
] as const;

export type AlertTrigger = (typeof ALERT_TRIGGERS)[number];

/** Channels with real delivery implemented */
export const SUPPORTED_ALERT_CHANNELS = ['EMAIL', 'WEBHOOK', 'SLACK'] as const;

export type SupportedAlertChannel = (typeof SUPPORTED_ALERT_CHANNELS)[number];

/** Shown in UI as coming soon — cannot deliver in production */
export const PLANNED_ALERT_CHANNELS = ['TEAMS', 'TELEGRAM', 'WHATSAPP', 'SMS'] as const;

export function isSupportedAlertChannel(channel: string): channel is SupportedAlertChannel {
  return (SUPPORTED_ALERT_CHANNELS as readonly string[]).includes(channel);
}

export function isAlertTrigger(value: string): value is AlertTrigger {
  return (ALERT_TRIGGERS as readonly string[]).includes(value);
}

export const ALERT_TRIGGER_LABELS: Record<AlertTrigger, string> = {
  MONITOR_DOWN: 'Monitor down',
  MONITOR_RECOVERY: 'Monitor recovered',
  HIGH_LATENCY: 'High latency',
  SSL_EXPIRY: 'SSL expiring',
  DNS_FAILURE: 'DNS failure',
};

/**
 * Triggers fired on individual check results (not incident open/close).
 * MONITOR_DOWN / MONITOR_RECOVERY are handled when incidents open or resolve.
 */
export function evaluateCheckTriggers(input: {
  monitorType: string;
  previousStatus: string;
  currentStatus: string;
  errorMessage?: string;
}): AlertTrigger[] {
  const { monitorType, previousStatus, currentStatus, errorMessage } = input;
  const triggers: AlertTrigger[] = [];
  const enteredDegraded = currentStatus === 'DEGRADED' && previousStatus !== 'DEGRADED';
  const enteredDown = currentStatus === 'DOWN' && previousStatus !== 'DOWN';

  if (enteredDegraded) {
    const sslExpiry =
      monitorType === 'SSL' &&
      !!errorMessage?.toLowerCase().match(/certificate|ssl|expir/);
    triggers.push(sslExpiry ? 'SSL_EXPIRY' : 'HIGH_LATENCY');
  }

  if (enteredDown && monitorType === 'DNS') {
    triggers.push('DNS_FAILURE');
  }

  return triggers;
}
