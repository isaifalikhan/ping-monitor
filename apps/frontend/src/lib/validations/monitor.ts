import { z } from 'zod';

export const MONITOR_TYPES = [
  'PING',
  'HTTP',
  'HTTPS',
  'TCP',
  'UDP',
  'SSL',
  'DNS',
  'API',
  'KEYWORD',
] as const;

export const createMonitorSchema = z.object({
  name: z.string().min(1, 'Name is required').max(100),
  description: z.string().max(500).optional(),
  type: z.enum(MONITOR_TYPES),
  target: z.string().min(1, 'Target is required').max(500),
  checkInterval: z.coerce.number().min(30).max(86400).default(60),
  timeout: z.coerce.number().min(5).max(300).default(30),
  retryCount: z.coerce.number().min(0).max(10).default(3),
  group: z.string().max(100).optional(),
  tags: z.string().optional(),
});

export type CreateMonitorFormData = z.infer<typeof createMonitorSchema>;
