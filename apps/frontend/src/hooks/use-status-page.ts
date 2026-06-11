'use client';

import {
  statusPageConfig,
  statusPageComponents,
  statusPageUptimeHistory,
  statusPageIncidents,
  statusPageMaintenance,
} from '@/lib/mock-data';

export function useStatusPage() {
  return {
    config: statusPageConfig,
    components: statusPageComponents,
    uptimeHistory: statusPageUptimeHistory,
    incidents: statusPageIncidents,
    maintenance: statusPageMaintenance,
  };
}
