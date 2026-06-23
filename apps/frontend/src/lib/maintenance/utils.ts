export type MaintenanceWindowLike = {
  id?: string;
  title?: string;
  startTime: string;
  endTime: string;
  monitorId?: string | null;
  monitor?: { id: string; name?: string } | null;
  pauseAlerts?: boolean;
  status?: 'UPCOMING' | 'ACTIVE' | 'COMPLETED';
};

export type MonitorMaintenanceState = {
  inMaintenance: boolean;
  alertsPaused: boolean;
  maintenanceWindow: { id: string; title: string } | null;
  displayStatus: string;
};

export function resolveMaintenanceStatus(
  startTime: string,
  endTime: string,
  now: Date = new Date(),
): 'UPCOMING' | 'ACTIVE' | 'COMPLETED' {
  const start = new Date(startTime);
  const end = new Date(endTime);
  if (end < now) return 'COMPLETED';
  if (start > now) return 'UPCOMING';
  return 'ACTIVE';
}

export function windowMonitorId(window: MaintenanceWindowLike): string | null {
  return window.monitorId ?? window.monitor?.id ?? null;
}

export function isWindowActive(window: MaintenanceWindowLike, now: Date = new Date()) {
  return resolveMaintenanceStatus(window.startTime, window.endTime, now) === 'ACTIVE';
}

export function windowAppliesToMonitor(window: MaintenanceWindowLike, monitorId: string) {
  const mid = windowMonitorId(window);
  return mid === null || mid === monitorId;
}

export function getActiveWindows<T extends MaintenanceWindowLike>(
  windows: T[],
  now: Date = new Date(),
): T[] {
  return windows.filter((w) => isWindowActive(w, now));
}

export function isMonitorInMaintenanceWindow(
  windows: MaintenanceWindowLike[],
  monitorId: string,
  now: Date = new Date(),
): boolean {
  return getActiveWindows(windows, now).some(
    (w) => windowAppliesToMonitor(w, monitorId),
  );
}

export function shouldPauseAlertsForMonitor(
  windows: MaintenanceWindowLike[],
  monitorId: string,
  now: Date = new Date(),
): boolean {
  return getActiveWindows(windows, now).some(
    (w) => w.pauseAlerts && windowAppliesToMonitor(w, monitorId),
  );
}

export function getMonitorMaintenanceState(
  monitor: { id: string; status: string; maintenanceMode: boolean },
  windows: MaintenanceWindowLike[],
  now: Date = new Date(),
): MonitorMaintenanceState {
  const active = getActiveWindows(windows, now).filter((w) =>
    windowAppliesToMonitor(w, monitor.id),
  );
  const inMaintenance = monitor.maintenanceMode || active.length > 0;
  const alertsPaused =
    monitor.maintenanceMode || active.some((w) => w.pauseAlerts);
  const primary = active[0];

  return {
    inMaintenance,
    alertsPaused,
    maintenanceWindow: primary?.id && primary.title
      ? { id: primary.id, title: primary.title }
      : null,
    displayStatus: inMaintenance ? 'MAINTENANCE' : monitor.status,
  };
}

export function countMonitorsInMaintenance(
  monitors: { id: string; status?: string; maintenanceMode: boolean }[],
  windows: MaintenanceWindowLike[],
  now: Date = new Date(),
): number {
  return monitors.filter((m) =>
    getMonitorMaintenanceState(
      { id: m.id, status: m.status ?? 'UNKNOWN', maintenanceMode: m.maintenanceMode },
      windows,
      now,
    ).inMaintenance,
  ).length;
}

export function countMonitorsWithPausedAlerts(
  monitors: { id: string; status?: string; maintenanceMode: boolean }[],
  windows: MaintenanceWindowLike[],
  now: Date = new Date(),
): number {
  return monitors.filter((m) =>
    getMonitorMaintenanceState(
      { id: m.id, status: m.status ?? 'UNKNOWN', maintenanceMode: m.maintenanceMode },
      windows,
      now,
    ).alertsPaused,
  ).length;
}
