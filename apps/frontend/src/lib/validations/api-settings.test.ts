/**
 * @jest-environment node
 */
import { updateSettingsSchema } from './api-settings';

describe('updateSettingsSchema', () => {
  it('accepts valid settings payload', () => {
    const result = updateSettingsSchema.safeParse({
      latencyThresholdMs: 1500,
      notificationEmail: 'ops@example.com',
      timezone: 'America/New_York',
      enablePublicStatusPage: true,
      preferences: {
        defaultCheckInterval: 120,
        statusPageSlug: 'acme-status',
        brandPrimaryColor: '#3b82f6',
      },
    });
    expect(result.success).toBe(true);
  });

  it('rejects invalid slug and hex color', () => {
    const slugResult = updateSettingsSchema.safeParse({
      preferences: { statusPageSlug: 'Invalid Slug!' },
    });
    expect(slugResult.success).toBe(false);

    const colorResult = updateSettingsSchema.safeParse({
      preferences: { brandPrimaryColor: 'blue' },
    });
    expect(colorResult.success).toBe(false);
  });

  it('allows empty notification email', () => {
    const result = updateSettingsSchema.safeParse({
      notificationEmail: '',
    });
    expect(result.success).toBe(true);
  });
});
