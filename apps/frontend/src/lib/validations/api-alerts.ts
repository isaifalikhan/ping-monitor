import { z } from 'zod';
import { ALERT_TRIGGERS, SUPPORTED_ALERT_CHANNELS } from '@/lib/alerts/triggers';

export const createAlertChannelSchema = z.object({
  name: z.string().min(1).max(100),
  channel: z.enum(SUPPORTED_ALERT_CHANNELS),
  config: z.record(z.string()).default({}),
});

export const createAlertRuleSchema = z.object({
  channelId: z.string().uuid(),
  trigger: z.enum(ALERT_TRIGGERS),
  monitorId: z.string().uuid().optional().nullable(),
});
