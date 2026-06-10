'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import { DEMO_MODE } from '@/lib/demo-mode';
import { useDemoStore, demoDelay } from '@/stores/demo-store';
import { toast } from '@/hooks/use-toast';
import { useAuthStore } from '@/stores/auth-store';

export function useSettings() {
  const settings = useDemoStore((s) => s.settings);
  const organization = useDemoStore((s) => s.organization);
  const authOrg = useAuthStore((s) => s.organization);

  const apiQuery = useQuery({
    queryKey: ['settings'],
    queryFn: () =>
      apiClient.get<{
        organization: { id: string; name: string; slug: string };
        settings: {
          latencyThresholdMs: number;
          notificationEmail?: string | null;
          timezone: string;
        };
      }>('/settings'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    return {
      data: {
        organization: authOrg
          ? { id: authOrg.id, name: authOrg.name, slug: authOrg.slug }
          : organization,
        settings,
      },
      isLoading: false,
      isError: false,
      error: null,
      refetch: async () => ({}),
    };
  }

  return apiQuery;
}

export function useUpdateSettings() {
  const qc = useQueryClient();
  const updateSettings = useDemoStore((s) => s.updateSettings);

  return useMutation({
    mutationFn: async (data: {
      latencyThresholdMs: number;
      notificationEmail: string;
      timezone: string;
    }) => {
      if (DEMO_MODE) {
        await demoDelay();
        updateSettings({
          latencyThresholdMs: Number(data.latencyThresholdMs),
          notificationEmail: String(data.notificationEmail ?? ''),
          timezone: String(data.timezone),
        });
        return;
      }
      return apiClient.patch('/settings', data);
    },
    onSuccess: () => {
      if (DEMO_MODE) {
        toast({ title: 'Settings saved', description: 'Organization preferences updated (demo).', variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['settings'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to save settings', variant: 'destructive' });
    },
  });
}
