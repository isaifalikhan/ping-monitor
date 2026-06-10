'use client';

import { useState } from 'react';
import { UserPlus, Trash2 } from 'lucide-react';
import { PageHeader } from '@/components/shared/page-header';
import { LoadingState } from '@/components/shared/loading-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { useTeam, useUpdateMember, useRemoveMember, useInviteMember } from '@/hooks/use-team';
import { useAuthStore } from '@/stores/auth-store';
import { DEMO_MODE } from '@/lib/demo-mode';

export default function TeamPage() {
  const { user } = useAuthStore();
  const { data, isLoading } = useTeam();
  const updateMember = useUpdateMember();
  const removeMember = useRemoveMember();
  const inviteMember = useInviteMember();

  const [showInvite, setShowInvite] = useState(false);
  const [inviteEmail, setInviteEmail] = useState('');
  const [inviteRole, setInviteRole] = useState('VIEWER');

  const canManage = DEMO_MODE || user?.role === 'OWNER' || user?.role === 'ADMIN';

  const handleInvite = () => {
    inviteMember.mutate(
      { email: inviteEmail, role: inviteRole },
      {
        onSuccess: () => {
          setShowInvite(false);
          setInviteEmail('');
        },
      },
    );
  };

  const handleRoleChange = (memberId: string, role: string) => {
    updateMember.mutate({ id: memberId, data: { role } });
  };

  const handleDisable = (memberId: string, isActive: boolean) => {
    updateMember.mutate({ id: memberId, data: { isActive: !isActive } });
  };

  const handleRemove = (memberId: string) => {
    if (!confirm('Remove this team member?')) return;
    removeMember.mutate(memberId);
  };

  if (isLoading) return <LoadingState />;

  return (
    <div className="space-y-6">
      <PageHeader
        title="Team"
        description="Manage team members and roles"
        actionButton={canManage ? (
          <Button onClick={() => setShowInvite(!showInvite)}>
            <UserPlus className="h-4 w-4 mr-2" />Invite User
          </Button>
        ) : undefined}
      />

      {showInvite && (
        <Card>
          <CardHeader><CardTitle className="text-base">Invite Team Member</CardTitle></CardHeader>
          <CardContent className="space-y-4 max-w-md">
            <div className="space-y-2">
              <Label>Email</Label>
              <Input type="email" value={inviteEmail} onChange={(e) => setInviteEmail(e.target.value)} />
            </div>
            <div className="space-y-2">
              <Label>Role</Label>
              <Select value={inviteRole} onChange={(e) => setInviteRole(e.target.value)}>
                <option value="VIEWER">Viewer</option>
                <option value="ADMIN">Admin</option>
              </Select>
            </div>
            <Button onClick={handleInvite} disabled={inviteMember.isPending || !inviteEmail}>Send Invitation</Button>
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader><CardTitle className="text-base">Members</CardTitle></CardHeader>
        <CardContent className="p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="px-6 py-3 text-left">Name</th>
                <th className="px-6 py-3 text-left">Email</th>
                <th className="px-6 py-3 text-left">Role</th>
                <th className="px-6 py-3 text-left">Status</th>
                {canManage && <th className="px-6 py-3 text-left">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {data?.members.map((m) => (
                <tr key={m.id} className="border-b last:border-0">
                  <td className="px-6 py-3 font-medium">{m.firstName} {m.lastName}</td>
                  <td className="px-6 py-3">{m.email}</td>
                  <td className="px-6 py-3">
                    {canManage && m.id !== user?.id ? (
                      <Select value={m.role} onChange={(e) => handleRoleChange(m.id, e.target.value)} className="w-[120px]">
                        <option value="VIEWER">Viewer</option>
                        <option value="ADMIN">Admin</option>
                        <option value="OWNER">Owner</option>
                      </Select>
                    ) : (
                      <Badge variant="outline">{m.role}</Badge>
                    )}
                  </td>
                  <td className="px-6 py-3">
                    <Badge variant={m.isActive ? 'success' : 'danger'}>{m.isActive ? 'Active' : 'Disabled'}</Badge>
                  </td>
                  {canManage && (
                    <td className="px-6 py-3">
                      {m.id !== user?.id && (
                        <div className="flex gap-2">
                          <Button size="sm" variant="outline" onClick={() => handleDisable(m.id, m.isActive)}>
                            {m.isActive ? 'Disable' : 'Enable'}
                          </Button>
                          <Button size="sm" variant="ghost" onClick={() => handleRemove(m.id)}>
                            <Trash2 className="h-4 w-4 text-destructive" />
                          </Button>
                        </div>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </CardContent>
      </Card>

      {!!data?.pendingInvitations.length && (
        <Card>
          <CardHeader><CardTitle className="text-base">Pending Invitations</CardTitle></CardHeader>
          <CardContent className="p-0">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-muted-foreground">
                  <th className="px-6 py-3 text-left">Email</th>
                  <th className="px-6 py-3 text-left">Role</th>
                  <th className="px-6 py-3 text-left">Expires</th>
                </tr>
              </thead>
              <tbody>
                {data.pendingInvitations.map((inv) => (
                  <tr key={inv.id} className="border-b last:border-0">
                    <td className="px-6 py-3">{inv.email}</td>
                    <td className="px-6 py-3">{inv.role}</td>
                    <td className="px-6 py-3 text-muted-foreground">{new Date(inv.expiresAt).toLocaleDateString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </CardContent>
        </Card>
      )}
    </div>
  );
}
