'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import type { Incident } from '@/lib/types';
import { DEMO_MODE } from '@/lib/demo-mode';
import { useDemoStore, demoDelay } from '@/stores/demo-store';
import { toast } from '@/hooks/use-toast';

export function useIncidents(status?: string) {
  const incidents = useDemoStore((s) => s.incidents);
  const getIncidents = useDemoStore((s) => s.getIncidents);

  const apiQuery = useQuery({
    queryKey: ['incidents', status],
    queryFn: () => apiClient.get<Incident[]>(`/incidents${status ? `?status=${status}` : ''}`),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    void incidents;
    return {
      data: getIncidents(status),
      isLoading: false,
      isError: false,
      error: null,
      refetch: async () => ({}),
    };
  }

  return apiQuery;
}

export function useAcknowledgeIncident() {
  const qc = useQueryClient();
  const acknowledgeIncident = useDemoStore((s) => s.acknowledgeIncident);

  return useMutation({
    mutationFn: async (id: string) => {
      if (DEMO_MODE) {
        await demoDelay();
        acknowledgeIncident(id);
        return;
      }
      return apiClient.patch(`/incidents/${id}/acknowledge`);
    },
    onSuccess: () => {
      if (DEMO_MODE) {
        toast({ title: 'Incident acknowledged', variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['incidents'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to acknowledge incident', variant: 'destructive' });
    },
  });
}

export function useResolveIncident() {
  const qc = useQueryClient();
  const resolveIncident = useDemoStore((s) => s.resolveIncident);

  return useMutation({
    mutationFn: async (id: string) => {
      if (DEMO_MODE) {
        await demoDelay();
        resolveIncident(id);
        return;
      }
      return apiClient.patch(`/incidents/${id}/resolve`);
    },
    onSuccess: () => {
      if (DEMO_MODE) {
        toast({ title: 'Incident resolved', variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['incidents'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to resolve incident', variant: 'destructive' });
    },
  });
}
