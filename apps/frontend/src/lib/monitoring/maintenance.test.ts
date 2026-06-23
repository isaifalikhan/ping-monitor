/**
 * @jest-environment node
 */
import {
  isMonitorInMaintenance,
  shouldPauseAlerts,
  type ActiveMaintenanceWindow,
} from './maintenance';

const windows: ActiveMaintenanceWindow[] = [
  { organizationId: 'org-1', monitorId: null, pauseAlerts: true },
  { organizationId: 'org-1', monitorId: 'mon-db', pauseAlerts: false },
  { organizationId: 'org-2', monitorId: null, pauseAlerts: true },
];

describe('cron maintenance helpers', () => {
  it('matches org-wide and monitor-specific windows', () => {
    expect(isMonitorInMaintenance(windows, 'org-1', 'mon-web')).toBe(true);
    expect(isMonitorInMaintenance(windows, 'org-1', 'mon-db')).toBe(true);
    expect(isMonitorInMaintenance(windows, 'org-2', 'mon-x')).toBe(true);
    expect(isMonitorInMaintenance(windows, 'org-3', 'mon-x')).toBe(false);
  });

  it('pauses alerts when any matching window has pauseAlerts', () => {
    expect(shouldPauseAlerts(windows, 'org-1', 'mon-web')).toBe(true);
    // Org-wide window still pauses alerts for mon-db even if its scoped window does not
    expect(shouldPauseAlerts(windows, 'org-1', 'mon-db')).toBe(true);
    expect(shouldPauseAlerts(windows, 'org-2', 'mon-x')).toBe(true);
  });

  it('does not pause when only matching windows have pauseAlerts false', () => {
    const scopedOnly: ActiveMaintenanceWindow[] = [
      { organizationId: 'org-1', monitorId: 'mon-db', pauseAlerts: false },
    ];
    expect(shouldPauseAlerts(scopedOnly, 'org-1', 'mon-db')).toBe(false);
    expect(shouldPauseAlerts(scopedOnly, 'org-1', 'mon-web')).toBe(false);
  });
});
