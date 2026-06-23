import nodemailer from 'nodemailer';
import { getSupabaseAdmin } from '@/lib/db/supabase';
import { isSupportedAlertChannel } from '@/lib/alerts/triggers';

const MAX_ATTEMPTS = 2;
const RETRY_DELAY_MS = 1000;

export type AlertDeliveryParams = {
  organizationId: string;
  trigger: string;
  monitorId?: string;
  monitorName: string;
  message: string;
};

type ChannelRecord = {
  id: string;
  channel: string;
  name: string;
  config: Record<string, string>;
  is_active: boolean;
};

function sleep(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function sendEmail(to: string, subject: string, html: string) {
  const host = process.env.SMTP_HOST;
  if (!host) {
    console.log(`[email] To: ${to} | ${subject}`);
    return;
  }
  const transporter = nodemailer.createTransport({
    host,
    port: parseInt(process.env.SMTP_PORT || '587', 10),
    secure: process.env.SMTP_SECURE === 'true',
    auth: process.env.SMTP_USER
      ? { user: process.env.SMTP_USER, pass: process.env.SMTP_PASSWORD }
      : undefined,
  });
  await transporter.sendMail({
    from: process.env.SMTP_FROM || 'netwatch@localhost',
    to,
    subject,
    html,
  });
}

async function deliverOnce(
  channel: ChannelRecord,
  params: AlertDeliveryParams,
): Promise<{ status: 'SENT' | 'FAILED'; detail: string }> {
  if (!isSupportedAlertChannel(channel.channel)) {
    return {
      status: 'FAILED',
      detail: `${channel.channel} is not supported yet — use Email, Webhook, or Slack`,
    };
  }

  const config = channel.config ?? {};

  if (channel.channel === 'EMAIL') {
    let email = config.email || config.address;
    if (!email) {
      const { getNotificationEmailFallback } = await import('@/lib/services/settings');
      email = (await getNotificationEmailFallback(params.organizationId)) ?? '';
    }
    if (!email) throw new Error('Email address not configured');
    await sendEmail(
      email,
      `NetWatch Alert: ${params.monitorName}`,
      `<p><strong>${params.trigger.replace(/_/g, ' ')}</strong></p><p>${params.message}</p>`,
    );
    return { status: 'SENT', detail: params.message };
  }

  const url = config.url || config.webhook || config.webhookUrl;
  if (!url) throw new Error('Webhook URL not configured');

  const payload =
    channel.channel === 'SLACK'
      ? {
          text: `*NetWatch* — ${params.monitorName}\n${params.message}`,
          blocks: [
            {
              type: 'section',
              text: {
                type: 'mrkdwn',
                text: `*${params.trigger.replace(/_/g, ' ')}*\n*${params.monitorName}*\n${params.message}`,
              },
            },
          ],
        }
      : {
          source: 'netwatch',
          trigger: params.trigger,
          monitor: params.monitorName,
          monitorId: params.monitorId,
          message: params.message,
        };

  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });
  if (!res.ok) throw new Error(`Webhook returned ${res.status}`);
  return { status: 'SENT', detail: params.message };
}

async function deliverWithRetry(
  channel: ChannelRecord,
  params: AlertDeliveryParams,
): Promise<{ status: 'SENT' | 'FAILED'; detail: string; attempts: number }> {
  let lastError = 'Delivery failed';
  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    try {
      const result = await deliverOnce(channel, params);
      return { ...result, attempts: attempt };
    } catch (err) {
      lastError = err instanceof Error ? err.message : 'Delivery failed';
      if (attempt < MAX_ATTEMPTS) await sleep(RETRY_DELAY_MS);
    }
  }
  return { status: 'FAILED', detail: lastError, attempts: MAX_ATTEMPTS };
}

async function logDelivery(
  params: AlertDeliveryParams,
  channelId: string,
  status: 'SENT' | 'FAILED',
  message: string,
) {
  const db = getSupabaseAdmin();
  await db.from('alert_logs').insert({
    organization_id: params.organizationId,
    channel_id: channelId,
    monitor_id: params.monitorId ?? null,
    trigger: params.trigger,
    status,
    message,
  });
}

export async function deliverToChannel(
  channel: ChannelRecord,
  params: AlertDeliveryParams,
): Promise<{ status: 'SENT' | 'FAILED'; detail: string }> {
  if (!channel.is_active) {
    return { status: 'FAILED', detail: 'Channel is disabled' };
  }
  const result = await deliverWithRetry(channel, params);
  const logMessage =
    result.status === 'FAILED' && result.attempts > 1
      ? `${result.detail} (after ${result.attempts} attempts)`
      : result.detail;
  await logDelivery(params, channel.id, result.status, logMessage);
  return { status: result.status, detail: logMessage };
}

export async function dispatchAlerts(params: AlertDeliveryParams) {
  const db = getSupabaseAdmin();
  const { data: rules, error } = await db
    .from('alert_rules')
    .select('*, alert_channels(*)')
    .eq('organization_id', params.organizationId)
    .eq('trigger', params.trigger)
    .eq('is_active', true);

  if (error || !rules?.length) return { sent: 0, failed: 0 };

  const applicable = rules.filter(
    (r) => !r.monitor_id || r.monitor_id === params.monitorId,
  );
  if (!applicable.length) return { sent: 0, failed: 0 };

  let sent = 0;
  let failed = 0;

  for (const rule of applicable) {
    const channel = rule.alert_channels as ChannelRecord | null;
    if (!channel) continue;
    const result = await deliverToChannel(channel, params);
    if (result.status === 'SENT') sent++;
    else failed++;
  }

  return { sent, failed };
}

export async function testAlertChannel(organizationId: string, channelId: string) {
  const db = getSupabaseAdmin();
  const { data: channel } = await db
    .from('alert_channels')
    .select('*')
    .eq('id', channelId)
    .eq('organization_id', organizationId)
    .single();
  if (!channel) throw new Error('Channel not found');

  const result = await deliverToChannel(
    {
      id: channel.id,
      channel: channel.channel,
      name: channel.name,
      config: channel.config as Record<string, string>,
      is_active: channel.is_active,
    },
    {
      organizationId,
      trigger: 'MONITOR_DOWN',
      monitorName: 'Test Monitor',
      message: 'This is a test alert from NetWatch',
    },
  );

  if (result.status === 'FAILED') {
    throw new Error(result.detail);
  }
  return { status: 'sent', message: `Test alert sent via ${channel.channel}` };
}
