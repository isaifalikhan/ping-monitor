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

const API_URL = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:4000/api/v1';

async function fetchPublicStatus(slug: string) {
  const response = await fetch(`${API_URL}/public/status/${slug}`);
  const json = await response.json();
  if (!response.ok || !json.success) {
    throw new Error(json.error || 'Failed to load status page');
  }
  return json.data as {
    config: typeof statusPageConfig;
    components: typeof statusPageComponents;
    uptimeHistory: typeof statusPageUptimeHistory;
    incidents: typeof statusPageIncidents;
    maintenance: typeof statusPageMaintenance;
  };
}

export function useStatusPage(slug = 'netwatch-demo') {
  const resolvedSlug = slug;

  const apiQuery = useQuery({
    queryKey: ['status-page', resolvedSlug],
    queryFn: () => fetchPublicStatus(resolvedSlug),
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
      slug: resolvedSlug,
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
