import { z } from 'zod';

const preferencesSchema = z
  .object({
    defaultCheckInterval: z.coerce.number().int().min(30).max(86400).optional(),
    defaultTimeout: z.coerce.number().int().min(5).max(300).optional(),
    retentionDays: z.coerce.number().int().min(7).max(365).optional(),
    statusPageSlug: z
      .string()
      .trim()
      .max(80)
      .regex(/^[a-z0-9]+(?:-[a-z0-9]+)*$/, 'Slug must be lowercase letters, numbers, and hyphens')
      .optional(),
    sessionTimeoutMinutes: z.coerce.number().int().min(15).max(1440).optional(),
    requireMfa: z.boolean().optional(),
    ipAllowlist: z.string().max(2000).optional(),
    brandPrimaryColor: z
      .string()
      .regex(/^#[0-9a-fA-F]{6}$/, 'Must be a hex color like #3b82f6')
      .optional(),
    brandLogoUrl: z.string().url().or(z.literal('')).optional(),
    slackWebhook: z.string().url().or(z.literal('')).optional(),
    pagerdutyKey: z.string().max(200).optional(),
    datadogApiKey: z.string().max(200).optional(),
  })
  .optional();

export const updateSettingsSchema = z.object({
  latencyThresholdMs: z.coerce.number().int().min(100).max(60000).optional(),
  notificationEmail: z.string().email().or(z.literal('')).nullable().optional(),
  timezone: z.string().min(1).max(64).optional(),
  enablePublicStatusPage: z.boolean().optional(),
  preferences: preferencesSchema,
});

export type UpdateSettingsInput = z.infer<typeof updateSettingsSchema>;
