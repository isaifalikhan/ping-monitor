/**
 * @jest-environment node
 */
import {
  countMonitorsInMaintenance,
  getActiveWindows,
  getMonitorMaintenanceState,
  isMonitorInMaintenanceWindow,
  resolveMaintenanceStatus,
  shouldPauseAlertsForMonitor,
  windowAppliesToMonitor,
} from './utils';

const now = new Date('2026-06-10T12:00:00.000Z');

describe('maintenance utils', () => {
  describe('resolveMaintenanceStatus', () => {
    it('returns ACTIVE when now is within the window', () => {
      expect(
        resolveMaintenanceStatus(
          '2026-06-10T10:00:00.000Z',
          '2026-06-10T14:00:00.000Z',
          now,
        ),
      ).toBe('ACTIVE');
    });

    it('returns UPCOMING when start is in the future', () => {
      expect(
        resolveMaintenanceStatus(
          '2026-06-10T13:00:00.000Z',
          '2026-06-10T15:00:00.000Z',
          now,
        ),
      ).toBe('UPCOMING');
    });

    it('returns COMPLETED when end is in the past', () => {
      expect(
        resolveMaintenanceStatus(
          '2026-06-10T08:00:00.000Z',
          '2026-06-10T11:00:00.000Z',
          now,
        ),
      ).toBe('COMPLETED');
    });
  });

  describe('windowAppliesToMonitor', () => {
    it('applies org-wide windows to any monitor', () => {
      expect(
        windowAppliesToMonitor(
          { startTime: '', endTime: '', monitorId: null },
          'mon-1',
        ),
      ).toBe(true);
    });

    it('applies only to the targeted monitor', () => {
      const window = {
        startTime: '',
        endTime: '',
        monitorId: 'mon-1',
      };
      expect(windowAppliesToMonitor(window, 'mon-1')).toBe(true);
      expect(windowAppliesToMonitor(window, 'mon-2')).toBe(false);
    });
  });

  describe('active windows and monitor state', () => {
    const activeGlobal = {
      id: 'w1',
      title: 'Network upgrade',
      startTime: '2026-06-10T10:00:00.000Z',
      endTime: '2026-06-10T14:00:00.000Z',
      monitorId: null,
      pauseAlerts: true,
    };

    const activeScoped = {
      id: 'w2',
      title: 'DB patch',
      startTime: '2026-06-10T11:00:00.000Z',
      endTime: '2026-06-10T13:00:00.000Z',
      monitorId: 'mon-db',
      pauseAlerts: false,
    };

    it('filters active windows', () => {
      const windows = [
        activeGlobal,
        activeScoped,
        {
          ...activeGlobal,
          id: 'w3',
          startTime: '2026-06-11T10:00:00.000Z',
          endTime: '2026-06-11T12:00:00.000Z',
        },
      ];
      expect(getActiveWindows(windows, now)).toHaveLength(2);
    });

    it('detects monitor in maintenance via window or flag', () => {
      expect(isMonitorInMaintenanceWindow([activeGlobal], 'mon-any', now)).toBe(true);
      expect(isMonitorInMaintenanceWindow([activeScoped], 'mon-other', now)).toBe(false);
      expect(
        getMonitorMaintenanceState(
          { id: 'mon-x', status: 'UP', maintenanceMode: true },
          [],
          now,
        ).inMaintenance,
      ).toBe(true);
    });

    it('pauses alerts for global windows with pauseAlerts', () => {
      expect(shouldPauseAlertsForMonitor([activeGlobal], 'mon-1', now)).toBe(true);
      expect(shouldPauseAlertsForMonitor([activeScoped], 'mon-db', now)).toBe(false);
    });

    it('overrides display status during maintenance', () => {
      const state = getMonitorMaintenanceState(
        { id: 'mon-1', status: 'UP', maintenanceMode: false },
        [activeGlobal],
        now,
      );
      expect(state.displayStatus).toBe('MAINTENANCE');
      expect(state.maintenanceWindow).toEqual({ id: 'w1', title: 'Network upgrade' });
    });

    it('counts monitors in maintenance', () => {
      const count = countMonitorsInMaintenance(
        [
          { id: 'mon-1', status: 'UP', maintenanceMode: false },
          { id: 'mon-db', status: 'UP', maintenanceMode: false },
          { id: 'mon-3', status: 'UP', maintenanceMode: true },
        ],
        [activeGlobal, activeScoped],
        now,
      );
      expect(count).toBe(3);
    });
  });
});
