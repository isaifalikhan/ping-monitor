'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import { DEMO_MODE } from '@/lib/demo-mode';
import { useDemoStore, demoDelay, type DemoSettings } from '@/stores/demo-store';
import { toast } from '@/hooks/use-toast';
import { useAuthStore } from '@/stores/auth-store';

const PREFERENCE_KEYS: (keyof DemoSettings)[] = [
  'defaultCheckInterval',
  'defaultTimeout',
  'retentionDays',
  'statusPageSlug',
  'sessionTimeoutMinutes',
  'requireMfa',
  'ipAllowlist',
  'brandPrimaryColor',
  'brandLogoUrl',
  'slackWebhook',
  'pagerdutyKey',
  'datadogApiKey',
];

function toApiPayload(data: Partial<DemoSettings>) {
  const payload: Record<string, unknown> = {};
  const preferences: Record<string, unknown> = {};

  if (data.latencyThresholdMs != null) {
    payload.latencyThresholdMs = Number(data.latencyThresholdMs);
  }
  if (data.notificationEmail !== undefined) {
    payload.notificationEmail = data.notificationEmail || null;
  }
  if (data.timezone !== undefined) payload.timezone = data.timezone;
  if (data.enablePublicStatusPage !== undefined) {
    payload.enablePublicStatusPage = data.enablePublicStatusPage;
  }

  for (const key of PREFERENCE_KEYS) {
    if (data[key] !== undefined) preferences[key] = data[key];
  }
  if (Object.keys(preferences).length > 0) payload.preferences = preferences;

  return payload;
}

type SettingsResponse = {
  organization: { id: string; name: string; slug: string };
  settings: DemoSettings;
};

export function useSettings() {
  const settings = useDemoStore((s) => s.settings);
  const organization = useDemoStore((s) => s.organization);
  const authOrg = useAuthStore((s) => s.organization);

  const apiQuery = useQuery({
    queryKey: ['settings'],
    queryFn: () => apiClient.get<SettingsResponse>('/settings'),
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
    mutationFn: async (data: Partial<DemoSettings>) => {
      if (DEMO_MODE) {
        await demoDelay();
        updateSettings({
          ...data,
          latencyThresholdMs:
            data.latencyThresholdMs != null ? Number(data.latencyThresholdMs) : undefined,
          defaultCheckInterval:
            data.defaultCheckInterval != null ? Number(data.defaultCheckInterval) : undefined,
          defaultTimeout: data.defaultTimeout != null ? Number(data.defaultTimeout) : undefined,
          retentionDays: data.retentionDays != null ? Number(data.retentionDays) : undefined,
          sessionTimeoutMinutes:
            data.sessionTimeoutMinutes != null ? Number(data.sessionTimeoutMinutes) : undefined,
        });
        return;
      }
      return apiClient.patch('/settings', toApiPayload(data));
    },
    onSuccess: () => {
      if (DEMO_MODE) {
        toast({
          title: 'Settings saved',
          description: 'Organization preferences updated (demo).',
          variant: 'success',
        });
      } else {
        qc.invalidateQueries({ queryKey: ['settings'] });
        toast({ title: 'Settings saved', variant: 'success' });
      }
    },
    onError: () => {
      toast({ title: 'Failed to save settings', variant: 'destructive' });
    },
  });
}
