'use client';

import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import { DEMO_MODE } from '@/lib/demo-mode';
import {
  statusPageConfig,
  statusPageComponents,
  statusPageUptimeHistory,
  statusPageIncidents,
  statusPageMaintenance,
} from '@/lib/mock-data';

export function useStatusPage(slug = 'netwatch-demo') {
  const apiQuery = useQuery({
    queryKey: ['status-page', slug],
    queryFn: () =>
      apiClient.get<{
        config: typeof statusPageConfig;
        components: typeof statusPageComponents;
        uptimeHistory: typeof statusPageUptimeHistory;
        incidents: typeof statusPageIncidents;
        maintenance: typeof statusPageMaintenance;
      }>(`/public/status/${slug}`),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    return {
      config: statusPageConfig,
      components: statusPageComponents,
      uptimeHistory: statusPageUptimeHistory,
      incidents: statusPageIncidents,
      maintenance: statusPageMaintenance,
      isLoading: false,
      isError: false,
    };
  }

  return {
    config: apiQuery.data?.config ?? {
      organizationName: 'Loading…',
      slug,
      overallStatus: 'UP' as const,
      overallUptime90d: 100,
      lastUpdated: new Date().toISOString(),
    },
    components: apiQuery.data?.components ?? [],
    uptimeHistory: apiQuery.data?.uptimeHistory ?? [],
    incidents: apiQuery.data?.incidents ?? [],
    maintenance: apiQuery.data?.maintenance ?? [],
    isLoading: apiQuery.isLoading,
    isError: apiQuery.isError,
  };
}
