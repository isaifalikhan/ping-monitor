'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import type { Monitor, Paginated } from '@/lib/types';
import { DEMO_MODE } from '@/lib/demo-mode';
import { useDemoStore, demoDelay } from '@/stores/demo-store';
import { toast } from '@/hooks/use-toast';

function filterMonitors(
  monitors: Monitor[],
  params?: { search?: string; status?: string; type?: string },
) {
  let items = monitors;
  if (params?.search) {
    const q = params.search.toLowerCase();
    items = items.filter(
      (m) =>
        m.name.toLowerCase().includes(q) ||
        m.target.toLowerCase().includes(q) ||
        m.tags.some((t) => t.toLowerCase().includes(q)),
    );
  }
  if (params?.status) items = items.filter((m) => m.status === params.status);
  if (params?.type) items = items.filter((m) => m.type === params.type);
  return items;
}

export function useMonitors(params?: { search?: string; status?: string; type?: string }) {
  const monitors = useDemoStore((s) => s.monitors);

  const apiQuery = useQuery({
    queryKey: ['monitors', params],
    queryFn: () => apiClient.get<Paginated<Monitor>>(`/monitors${buildQs(params)}`),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    const items = filterMonitors(monitors, params);
    return {
      data: { items, total: items.length, page: 1, limit: 100, totalPages: 1 },
      isLoading: false,
      isError: false,
      error: null,
      refetch: async () => ({}),
    };
  }

  return apiQuery;
}

export function useMonitor(id: string) {
  const monitors = useDemoStore((s) => s.monitors);
  const monitorChecks = useDemoStore((s) => s.monitorChecks);
  const incidents = useDemoStore((s) => s.incidents);
  const getMonitorDetail = useDemoStore((s) => s.getMonitorDetail);

  const apiQuery = useQuery({
    queryKey: ['monitor', id],
    queryFn: () =>
      apiClient.get<{
        monitor: Monitor;
        checks: unknown[];
        incidents: unknown[];
        alertHistory: unknown[];
      }>(`/monitors/${id}`),
    enabled: !DEMO_MODE && !!id,
  });

  if (DEMO_MODE) {
    void monitors;
    void monitorChecks;
    void incidents;
    return {
      data: id ? getMonitorDetail(id) ?? undefined : undefined,
      isLoading: false,
      isError: false,
      error: null,
      refetch: async () => ({}),
    };
  }

  return apiQuery;
}

export function useCreateMonitor() {
  const qc = useQueryClient();
  const addMonitor = useDemoStore((s) => s.addMonitor);

  return useMutation({
    mutationFn: async (data: Record<string, unknown>) => {
      if (DEMO_MODE) {
        await demoDelay();
        return addMonitor(data);
      }
      return apiClient.post<Monitor>('/monitors', data);
    },
    onSuccess: (monitor) => {
      if (DEMO_MODE) {
        toast({ title: 'Monitor created', description: `"${monitor.name}" is now being tracked (demo).`, variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['monitors'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to create monitor', variant: 'destructive' });
    },
  });
}

export function useUpdateMonitor() {
  const qc = useQueryClient();
  const updateMonitor = useDemoStore((s) => s.updateMonitor);

  return useMutation({
    mutationFn: async ({ id, data }: { id: string; data: Record<string, unknown> }) => {
      if (DEMO_MODE) {
        await demoDelay();
        return updateMonitor(id, data);
      }
      return apiClient.patch<Monitor>(`/monitors/${id}`, data);
    },
    onSuccess: (monitor, { id }) => {
      if (DEMO_MODE) {
        const action = monitor.isActive === false ? 'paused' : 'updated';
        toast({ title: `Monitor ${action}`, description: `"${monitor.name}" saved (demo).`, variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['monitors'] });
        qc.invalidateQueries({ queryKey: ['monitor', id] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to update monitor', variant: 'destructive' });
    },
  });
}

export function useDeleteMonitor() {
  const qc = useQueryClient();
  const deleteMonitor = useDemoStore((s) => s.deleteMonitor);

  return useMutation({
    mutationFn: async (id: string) => {
      if (DEMO_MODE) {
        await demoDelay();
        deleteMonitor(id);
        return;
      }
      return apiClient.delete(`/monitors/${id}`);
    },
    onSuccess: () => {
      if (DEMO_MODE) {
        toast({ title: 'Monitor deleted', variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['monitors'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to delete monitor', variant: 'destructive' });
    },
  });
}

function buildQs(params?: { search?: string; status?: string; type?: string }) {
  const qs = new URLSearchParams();
  if (params?.search) qs.set('search', params.search);
  if (params?.status) qs.set('status', params.status);
  if (params?.type) qs.set('type', params.type);
  const query = qs.toString();
  return query ? `?${query}` : '';
}
