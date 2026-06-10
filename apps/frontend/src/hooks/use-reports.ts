'use client';

import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import { DEMO_MODE } from '@/lib/demo-mode';
import { useDemoStore } from '@/stores/demo-store';

export interface ReportSummaryData {
  summary: {
    totalMonitors: number;
    avgUptime: number;
    avgResponseTime: number;
    totalIncidents: number;
    openIncidents: number;
  };
  responseTrend: { day: string; ms: number }[];
  incidentTrend: { day: string; count: number }[];
  monitors: { name: string; uptime: number; status: string }[];
}

export function useReportSummary(from?: string, to?: string) {
  const monitors = useDemoStore((s) => s.monitors);
  const incidents = useDemoStore((s) => s.incidents);
  const getReportSummary = useDemoStore((s) => s.getReportSummary);

  const apiQuery = useQuery({
    queryKey: ['reports', from, to],
    queryFn: () => {
      const qs = new URLSearchParams();
      if (from) qs.set('from', from);
      if (to) qs.set('to', to);
      const query = qs.toString();
      return apiClient.get<ReportSummaryData>(`/reports/summary${query ? `?${query}` : ''}`);
    },
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    void monitors;
    void incidents;
    void from;
    void to;
    return {
      data: getReportSummary(from, to),
      isLoading: false,
      isError: false,
      error: null,
      refetch: async () => ({}),
    };
  }

  return apiQuery;
}
