'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/api-client';
import type { TeamMember } from '@/lib/types';
import { DEMO_MODE } from '@/lib/demo-mode';
import { useDemoStore, demoDelay } from '@/stores/demo-store';
import { toast } from '@/hooks/use-toast';

export function useTeam() {
  const teamMembers = useDemoStore((s) => s.teamMembers);
  const pendingInvitations = useDemoStore((s) => s.pendingInvitations);

  const apiQuery = useQuery({
    queryKey: ['team'],
    queryFn: () =>
      apiClient.get<{
        members: TeamMember[];
        pendingInvitations: { id: string; email: string; role: string; expiresAt: string }[];
      }>('/organizations/team'),
    enabled: !DEMO_MODE,
  });

  if (DEMO_MODE) {
    return {
      data: { members: teamMembers, pendingInvitations },
      isLoading: false,
      isError: false,
      error: null,
      refetch: async () => ({}),
    };
  }

  return apiQuery;
}

export function useUpdateMember() {
  const qc = useQueryClient();
  const updateMember = useDemoStore((s) => s.updateMember);

  return useMutation({
    mutationFn: async ({ id, data }: { id: string; data: Record<string, unknown> }) => {
      if (DEMO_MODE) {
        await demoDelay();
        updateMember(id, data);
        return;
      }
      return apiClient.patch(`/organizations/team/${id}`, data);
    },
    onSuccess: (_, { data }) => {
      if (DEMO_MODE) {
        const msg = data.isActive === false ? 'Member disabled' : data.isActive === true ? 'Member enabled' : 'Role updated';
        toast({ title: msg, variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['team'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to update member', variant: 'destructive' });
    },
  });
}

export function useRemoveMember() {
  const qc = useQueryClient();
  const removeMember = useDemoStore((s) => s.removeMember);

  return useMutation({
    mutationFn: async (id: string) => {
      if (DEMO_MODE) {
        await demoDelay();
        removeMember(id);
        return;
      }
      return apiClient.delete(`/organizations/team/${id}`);
    },
    onSuccess: () => {
      if (DEMO_MODE) {
        toast({ title: 'Member removed', variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['team'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to remove member', variant: 'destructive' });
    },
  });
}

export function useInviteMember() {
  const qc = useQueryClient();
  const inviteMember = useDemoStore((s) => s.inviteMember);

  return useMutation({
    mutationFn: async (data: { email: string; role: string }) => {
      if (DEMO_MODE) {
        await demoDelay();
        inviteMember(data);
        return;
      }
      return apiClient.post('/auth/invite', data);
    },
    onSuccess: (_, { email }) => {
      if (DEMO_MODE) {
        toast({ title: 'Invitation sent', description: `Invite sent to ${email} (demo).`, variant: 'success' });
      } else {
        qc.invalidateQueries({ queryKey: ['team'] });
      }
    },
    onError: () => {
      toast({ title: 'Failed to send invitation', variant: 'destructive' });
    },
  });
}
