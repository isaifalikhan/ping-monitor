'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import type { AlertChannel, AlertRule } from '@/lib/types';
import { DEMO_MODE } from '@/lib/demo-mode';
import { alertStats } from '@/lib/mock-data';
import { useDemoStore, demoDelay } from '@/stores/demo-store';
import { toast } from '@/hooks/use-toast';

export function useAlertChannels() {
  const alertChannels = useDemoStore((s) => s.alertChannels);

  const apiQuery = useQuery({
    queryKey: ['alert-channels'],
    queryFn: () => apiClient.get<AlertChannel[]>('/alerts/channels'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    return {
      data: alertChannels,
      isLoading: false,
      isError: false,
      error: null,
      refetch: async () => ({}),
    };
  }

  return apiQuery;
}

export function useAlertRules() {
  const alertRules = useDemoStore((s) => s.alertRules);

  const apiQuery = useQuery({
    queryKey: ['alert-rules'],
    queryFn: () => apiClient.get<AlertRule[]>('/alerts/rules'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    return {
      data: alertRules,
      isLoading: false,
      isError: false,
      error: null,
      refetch: async () => ({}),
    };
  }

  return apiQuery;
}

export function useCreateAlertChannel() {
  const qc = useQueryClient();
  const addAlertChannel = useDemoStore((s) => s.addAlertChannel);

  return useMutation({
    mutationFn: async (data: Record<string, unknown>) => {
      if (DEMO_MODE) {
        await demoDelay();
        return addAlertChannel({
          channel: String(data.channel),
          name: String(data.name),
          config: data.config as Record<string, unknown>,
        });
      }
      return apiClient.post<AlertChannel>('/alerts/channels', data);
    },
    onSuccess: (channel) => {
      if (DEMO_MODE) {
        toast({ title: 'Alert channel added', description: `"${channel.name}" configured (demo).`, variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['alert-channels'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to create channel', variant: 'destructive' });
    },
  });
}

export function useCreateAlertRule() {
  const qc = useQueryClient();
  const addAlertRule = useDemoStore((s) => s.addAlertRule);

  return useMutation({
    mutationFn: async (data: Record<string, unknown>) => {
      if (DEMO_MODE) {
        await demoDelay();
        return addAlertRule({
          channelId: String(data.channelId),
          trigger: String(data.trigger),
          monitorId: data.monitorId ? String(data.monitorId) : undefined,
        });
      }
      return apiClient.post<AlertRule>('/alerts/rules', data);
    },
    onSuccess: () => {
      if (DEMO_MODE) {
        toast({ title: 'Alert rule created', variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['alert-rules'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to create rule', variant: 'destructive' });
    },
  });
}

export function useAlertDeliveries() {
  const alertDeliveries = useDemoStore((s) => s.alertDeliveries);
  if (!DEMO_MODE) return { data: [], isLoading: false };
  return { data: alertDeliveries, isLoading: false };
}

export function useAlertStats() {
  if (!DEMO_MODE) {
    return {
      totalSent: 0,
      totalFailed: 0,
      successRate: 0,
      last24h: 0,
      avgDeliveryMs: 0,
    };
  }
  return alertStats;
}

export function useTestAlert() {
  const testAlertChannel = useDemoStore((s) => s.testAlertChannel);

  return useMutation({
    mutationFn: async (channelId: string) => {
      if (DEMO_MODE) {
        await demoDelay(500);
        return testAlertChannel(channelId);
      }
      return apiClient.post<{ status: string; message: string }>(`/alerts/channels/${channelId}/test`);
    },
    onSuccess: (result) => {
      toast({
        title: 'Test alert sent',
        description: result.message,
        variant: result.status === 'sent' ? 'success' : 'destructive',
      });
    },
    onError: () => {
      toast({ title: 'Test alert failed', variant: 'destructive' });
    },
  });
}
