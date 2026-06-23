/**
 * @jest-environment node
 */
import { createMaintenanceSchema } from './api-maintenance';

describe('createMaintenanceSchema', () => {
  it('accepts valid maintenance window', () => {
    const result = createMaintenanceSchema.safeParse({
      title: 'Firewall upgrade',
      startTime: '2026-06-10T10:00:00.000Z',
      endTime: '2026-06-10T12:00:00.000Z',
      pauseAlerts: true,
    });
    expect(result.success).toBe(true);
  });

  it('rejects end time before start time', () => {
    const result = createMaintenanceSchema.safeParse({
      title: 'Bad window',
      startTime: '2026-06-10T12:00:00.000Z',
      endTime: '2026-06-10T10:00:00.000Z',
    });
    expect(result.success).toBe(false);
    if (!result.success) {
      expect(result.error.issues.some((i) => i.path.includes('endTime'))).toBe(true);
    }
  });
});
