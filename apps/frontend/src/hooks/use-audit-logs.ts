'use client';

import { useDemoStore } from '@/stores/demo-store';
import { DEMO_MODE } from '@/lib/demo-mode';
import type { AuditLogEntry } from '@/lib/types';

export function useAuditLogs(category?: string) {
  const auditLogs = useDemoStore((s) => s.auditLogs);

  if (!DEMO_MODE) {
    return { data: [] as AuditLogEntry[], isLoading: false };
  }

  const data = category
    ? auditLogs.filter((l) => l.category === category)
    : auditLogs;

  return { data, isLoading: false };
}
