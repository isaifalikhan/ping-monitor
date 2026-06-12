'use client';

import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import { useDemoStore } from '@/stores/demo-store';
import { DEMO_MODE } from '@/lib/demo-mode';

type DashboardStats = {
  totalMonitors: number;
  online: number;
  offline: number;
  degraded: number;
  activeIncidents: number;
  uptimePercent: number;
  avgResponseTime: number;
};

type DashboardCharts = {
  responseTimeTrend: { time: string; ms: number }[];
  uptimeTrend: { day: string; uptime: number }[];
  uptime24h: { hour: string; uptime: number }[];
  incidentTrend: { day: string; incidents: number }[];
  monitorDistribution: { name: string; value: number; color: string }[];
  statusDistribution: { name: string; value: number; color: string }[];
};

export function useDashboardStats() {
  const monitors = useDemoStore((s) => s.monitors);
  const incidents = useDemoStore((s) => s.incidents);
  const getDashboardStats = useDemoStore((s) => s.getDashboardStats);

  const apiQuery = useQuery({
    queryKey: ['dashboard', 'stats'],
    queryFn: () => apiClient.get<DashboardStats>('/dashboard/stats'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    void monitors;
    void incidents;
    return { stats: getDashboardStats(), isLoading: false };
  }

  return {
    stats: apiQuery.data ?? {
      totalMonitors: 0,
      online: 0,
      offline: 0,
      degraded: 0,
      activeIncidents: 0,
      uptimePercent: 0,
      avgResponseTime: 0,
    },
    isLoading: apiQuery.isLoading,
  };
}

export function useDashboardCharts() {
  const monitors = useDemoStore((s) => s.monitors);
  const incidents = useDemoStore((s) => s.incidents);
  const getDashboardCharts = useDemoStore((s) => s.getDashboardCharts);

  const apiQuery = useQuery({
    queryKey: ['dashboard', 'charts'],
    queryFn: () => apiClient.get<DashboardCharts>('/dashboard/charts'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    void monitors;
    void incidents;
    return getDashboardCharts();
  }

  return (
    apiQuery.data ?? {
      responseTimeTrend: [],
      uptimeTrend: [],
      uptime24h: [],
      incidentTrend: [],
      monitorDistribution: [],
      statusDistribution: [],
    }
  );
}

export function useDashboardRecentAlerts() {
  const recentAlerts = useDemoStore((s) => s.recentAlerts);

  const apiQuery = useQuery({
    queryKey: ['dashboard', 'recent-alerts'],
    queryFn: () =>
      apiClient.get<
        { id: string; monitor: string; type: string; channel: string; time: string; status: string }[]
      >('/dashboard/recent-alerts'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) return recentAlerts;
  return apiQuery.data ?? [];
}

export function useDashboardRecentIncidents() {
  const incidents = useDemoStore((s) => s.incidents);
  const getRecentIncidents = useDemoStore((s) => s.getRecentIncidents);

  const apiQuery = useQuery({
    queryKey: ['dashboard', 'recent-incidents'],
    queryFn: () =>
      apiClient.get<
        { id: string; monitor: string; status: string; duration: string; started: string }[]
      >('/dashboard/recent-incidents'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    void incidents;
    return getRecentIncidents();
  }
  return apiQuery.data ?? [];
}

export function useDashboardRecentChecks() {
  const monitors = useDemoStore((s) => s.monitors);
  const monitorChecks = useDemoStore((s) => s.monitorChecks);
  const getRecentChecks = useDemoStore((s) => s.getRecentChecks);

  const apiQuery = useQuery({
    queryKey: ['dashboard', 'recent-checks'],
    queryFn: () =>
      apiClient.get<
        { id: string; monitor: string; status: string; responseTime: number | null; time: string }[]
      >('/dashboard/recent-checks'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    void monitors;
    void monitorChecks;
    return getRecentChecks();
  }
  return apiQuery.data ?? [];
}
