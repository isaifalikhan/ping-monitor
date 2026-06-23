'use client';

import { useMemo } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import type { MaintenanceWindow } from '@/lib/types';
import { DEMO_MODE } from '@/lib/demo-mode';
import { useDemoStore, demoDelay } from '@/stores/demo-store';
import { toast } from '@/hooks/use-toast';
import { resolveMaintenanceStatus } from '@/lib/maintenance/utils';

function withResolvedStatus(windows: MaintenanceWindow[]): MaintenanceWindow[] {
  return windows.map((w) => ({
    ...w,
    status: resolveMaintenanceStatus(w.startTime, w.endTime),
  }));
}

export function useMaintenanceWindows() {
  const maintenanceWindows = useDemoStore((s) => s.maintenanceWindows);

  const apiQuery = useQuery({
    queryKey: ['maintenance'],
    queryFn: () => apiClient.get<MaintenanceWindow[]>('/maintenance'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    return {
      data: withResolvedStatus(maintenanceWindows),
      isLoading: false,
      isError: false,
      error: null,
      refetch: async () => ({}),
    };
  }

  return apiQuery;
}

export function useActiveMaintenance() {
  const { data: windows, ...rest } = useMaintenanceWindows();
  const active = useMemo(
    () => (windows ?? []).filter((w) => w.status === 'ACTIVE'),
    [windows],
  );
  return { data: active, ...rest };
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
        qc.invalidateQueries({ queryKey: ['monitors'] });
        toast({ title: 'Maintenance scheduled', description: `"${window.title}" created.`, variant: 'success' });
      }
    },
    onError: () => {
      toast({ title: 'Failed to create maintenance window', variant: 'destructive' });
    },
  });
}

export function useCancelMaintenance() {
  const qc = useQueryClient();
  const cancelMaintenance = useDemoStore((s) => s.cancelMaintenance);

  return useMutation({
    mutationFn: async (id: string) => {
      if (DEMO_MODE) {
        await demoDelay();
        return cancelMaintenance(id);
      }
      return apiClient.delete<{ id: string; action: string }>(`/maintenance/${id}`);
    },
    onSuccess: (result) => {
      if (DEMO_MODE) {
        toast({
          title: result.action === 'deleted' ? 'Maintenance cancelled' : 'Maintenance ended',
          variant: 'success',
        });
      } else {
        qc.invalidateQueries({ queryKey: ['maintenance'] });
        qc.invalidateQueries({ queryKey: ['monitors'] });
        toast({
          title: result.action === 'deleted' ? 'Maintenance cancelled' : 'Maintenance ended early',
          variant: 'success',
        });
      }
    },
    onError: (err: Error) => {
      toast({ title: err.message || 'Failed to cancel maintenance', variant: 'destructive' });
    },
  });
}
