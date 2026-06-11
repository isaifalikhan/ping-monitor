'use client';

/**
 * MOCK DATA ONLY - replace with real API later
 */
import { useDemoStore } from '@/stores/demo-store';
import { DEMO_MODE } from '@/lib/demo-mode';
import {
  responseTimeTrend,
  uptimeTrend,
  uptime24h,
  incidentTrend,
  monitorDistribution,
  statusDistribution,
} from '@/lib/mock-data';

export function useDashboardStats() {
  const monitors = useDemoStore((s) => s.monitors);
  const incidents = useDemoStore((s) => s.incidents);
  const getDashboardStats = useDemoStore((s) => s.getDashboardStats);

  if (!DEMO_MODE) {
    return {
      stats: {
        totalMonitors: 0,
        online: 0,
        offline: 0,
        degraded: 0,
        activeIncidents: 0,
        uptimePercent: 0,
        avgResponseTime: 0,
      },
    };
  }

  void monitors;
  void incidents;
  return { stats: getDashboardStats() };
}

export function useDashboardCharts() {
  return {
    responseTimeTrend,
    uptimeTrend,
    uptime24h,
    incidentTrend,
    monitorDistribution,
    statusDistribution,
  };
}

export function useDashboardRecentAlerts() {
  const recentAlerts = useDemoStore((s) => s.recentAlerts);
  return DEMO_MODE ? recentAlerts : [];
}

export function useDashboardRecentIncidents() {
  const incidents = useDemoStore((s) => s.incidents);
  const getRecentIncidents = useDemoStore((s) => s.getRecentIncidents);
  if (!DEMO_MODE) return [];
  void incidents;
  return getRecentIncidents();
}

export function useDashboardRecentChecks() {
  const monitors = useDemoStore((s) => s.monitors);
  const monitorChecks = useDemoStore((s) => s.monitorChecks);
  const getRecentChecks = useDemoStore((s) => s.getRecentChecks);
  if (!DEMO_MODE) return [];
  void monitors;
  void monitorChecks;
  return getRecentChecks();
}
