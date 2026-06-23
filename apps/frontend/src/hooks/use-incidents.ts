'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import type { Incident } from '@/lib/types';
import { DEMO_MODE } from '@/lib/demo-mode';
import { useDemoStore, demoDelay } from '@/stores/demo-store';
import { toast } from '@/hooks/use-toast';
import { useAuthStore } from '@/stores/auth-store';

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

function actorName() {
  const user = useAuthStore.getState().user;
  return user ? `${user.firstName} ${user.lastName}` : 'You';
}

export function useAcknowledgeIncident() {
  const qc = useQueryClient();
  const acknowledgeIncident = useDemoStore((s) => s.acknowledgeIncident);

  return useMutation({
    mutationFn: async (id: string) => {
      if (DEMO_MODE) {
        await demoDelay();
        acknowledgeIncident(id, actorName());
        return;
      }
      return apiClient.patch<Incident>(`/incidents/${id}/acknowledge`);
    },
    onSuccess: () => {
      toast({ title: 'Incident acknowledged', variant: 'success' });
      qc.invalidateQueries({ queryKey: ['incidents'] });
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
    mutationFn: async ({
      id,
      rootCause,
      notes,
    }: {
      id: string;
      rootCause?: string;
      notes?: string;
    }) => {
      if (DEMO_MODE) {
        await demoDelay();
        resolveIncident(id, actorName(), { rootCause, notes });
        return;
      }
      return apiClient.patch<Incident>(`/incidents/${id}/resolve`, { rootCause, notes });
    },
    onSuccess: () => {
      toast({ title: 'Incident resolved', variant: 'success' });
      qc.invalidateQueries({ queryKey: ['incidents'] });
    },
    onError: () => {
      toast({ title: 'Failed to resolve incident', variant: 'destructive' });
    },
  });
}

export function useUpdateIncident() {
  const qc = useQueryClient();
  const updateIncident = useDemoStore((s) => s.updateIncident);

  return useMutation({
    mutationFn: async ({
      id,
      data,
    }: {
      id: string;
      data: {
        assignedUserId?: string | null;
        severity?: string;
        rootCause?: string | null;
        notes?: string | null;
        note?: string;
      };
    }) => {
      if (DEMO_MODE) {
        await demoDelay();
        updateIncident(id, data, actorName());
        return;
      }
      return apiClient.patch<Incident>(`/incidents/${id}`, data);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['incidents'] });
    },
    onError: () => {
      toast({ title: 'Failed to update incident', variant: 'destructive' });
    },
  });
}
