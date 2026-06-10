'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import type { MaintenanceWindow } from '@/lib/types';
import { DEMO_MODE } from '@/lib/demo-mode';
import { useDemoStore, demoDelay } from '@/stores/demo-store';
import { toast } from '@/hooks/use-toast';

export function useMaintenanceWindows() {
  const maintenanceWindows = useDemoStore((s) => s.maintenanceWindows);

  const apiQuery = useQuery({
    queryKey: ['maintenance'],
    queryFn: () => apiClient.get<MaintenanceWindow[]>('/maintenance'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    return {
      data: maintenanceWindows,
      isLoading: false,
      isError: false,
      error: null,
      refetch: async () => ({}),
    };
  }

  return apiQuery;
}

export function useCreateMaintenance() {
  const qc = useQueryClient();
  const createMaintenance = useDemoStore((s) => s.createMaintenance);

  return useMutation({
    mutationFn: async (data: Record<string, unknown>) => {
      if (DEMO_MODE) {
        await demoDelay();
        return createMaintenance(data);
      }
      return apiClient.post<MaintenanceWindow>('/maintenance', data);
    },
    onSuccess: (window) => {
      if (DEMO_MODE) {
        toast({ title: 'Maintenance scheduled', description: `"${window.title}" created (demo).`, variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['maintenance'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to create maintenance window', variant: 'destructive' });
    },
  });
}
