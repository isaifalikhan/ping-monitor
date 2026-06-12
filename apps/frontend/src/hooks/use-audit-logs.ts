'use client';

import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import { useDemoStore } from '@/stores/demo-store';
import { DEMO_MODE } from '@/lib/demo-mode';
import type { AuditLogEntry } from '@/lib/types';

export function useAuditLogs(category?: string) {
  const auditLogs = useDemoStore((s) => s.auditLogs);

  const apiQuery = useQuery({
    queryKey: ['audit-logs', category],
    queryFn: () =>
      apiClient.get<AuditLogEntry[]>('/audit-logs', {
        params: category ? { category } : undefined,
      }),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    const data = category ? auditLogs.filter((l) => l.category === category) : auditLogs;
    return { data, isLoading: false };
  }

  return { data: apiQuery.data ?? [], isLoading: apiQuery.isLoading };
}
