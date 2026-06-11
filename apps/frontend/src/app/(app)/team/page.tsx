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
import { Avatar } from '@/components/ui/avatar';
import { useTeam, useUpdateMember, useRemoveMember, useInviteMember, usePermissionsMatrix } from '@/hooks/use-team';
import { useAuthStore } from '@/stores/auth-store';
import { DEMO_MODE } from '@/lib/demo-mode';

function formatLastLogin(date?: string | null) {
  if (!date) return 'Never';
  const diff = Date.now() - new Date(date).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  return new Date(date).toLocaleDateString();
}

export default function TeamPage() {
  const { user } = useAuthStore();
  const { data, isLoading } = useTeam();
  const permissions = usePermissionsMatrix();
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
      { onSuccess: () => { setShowInvite(false); setInviteEmail(''); } },
    );
  };

  if (isLoading) return <LoadingState />;

  return (
    <div className="space-y-6">
      <PageHeader
        title="Team"
        description="Manage team members, roles, and permissions"
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
        <CardHeader><CardTitle className="text-base">Team Members</CardTitle></CardHeader>
        <CardContent className="p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="px-6 py-3 text-left">Member</th>
                <th className="px-6 py-3 text-left">Role</th>
                <th className="px-6 py-3 text-left">Title</th>
                <th className="px-6 py-3 text-left">Last Login</th>
                <th className="px-6 py-3 text-left">Status</th>
                {canManage && <th className="px-6 py-3 text-left">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {data?.members.map((m) => (
                <tr key={m.id} className="border-b last:border-0 hover:bg-muted/50 transition-colors">
                  <td className="px-6 py-3">
                    <div className="flex items-center gap-3">
                      <Avatar
                        firstName={m.firstName}
                        lastName={m.lastName}
                        color={m.avatarColor}
                        status={m.status}
                      />
                      <div>
                        <p className="font-medium">{m.firstName} {m.lastName}</p>
                        <p className="text-xs text-muted-foreground">{m.email}</p>
                      </div>
                    </div>
                  </td>
                  <td className="px-6 py-3">
                    {canManage && m.id !== user?.id ? (
                      <Select value={m.role} onChange={(e) => updateMember.mutate({ id: m.id, data: { role: e.target.value } })} className="w-[120px]">
                        <option value="VIEWER">Viewer</option>
                        <option value="ADMIN">Admin</option>
                        <option value="OWNER">Owner</option>
                      </Select>
                    ) : (
                      <Badge variant="outline">{m.role}</Badge>
                    )}
                  </td>
                  <td className="px-6 py-3 text-muted-foreground">{m.title ?? '—'}</td>
                  <td className="px-6 py-3 text-muted-foreground">{formatLastLogin(m.lastLoginAt)}</td>
                  <td className="px-6 py-3">
                    <Badge variant={m.isActive ? 'success' : 'danger'}>{m.isActive ? 'Active' : 'Disabled'}</Badge>
                  </td>
                  {canManage && (
                    <td className="px-6 py-3">
                      {m.id !== user?.id && (
                        <div className="flex gap-2">
                          <Button size="sm" variant="outline" onClick={() => updateMember.mutate({ id: m.id, data: { isActive: !m.isActive } })}>
                            {m.isActive ? 'Disable' : 'Enable'}
                          </Button>
                          <Button size="sm" variant="ghost" onClick={() => { if (confirm('Remove this team member?')) removeMember.mutate(m.id); }}>
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
                    <td className="px-6 py-3"><Badge variant="outline">{inv.role}</Badge></td>
                    <td className="px-6 py-3 text-muted-foreground">{new Date(inv.expiresAt).toLocaleDateString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </CardContent>
        </Card>
      )}

      {permissions.length > 0 && (
        <Card>
          <CardHeader><CardTitle className="text-base">Permissions Matrix</CardTitle></CardHeader>
          <CardContent className="p-0 overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-muted-foreground">
                  <th className="px-6 py-3 text-left">Permission</th>
                  <th className="px-6 py-3 text-center">Owner</th>
                  <th className="px-6 py-3 text-center">Admin</th>
                  <th className="px-6 py-3 text-center">Network Eng.</th>
                  <th className="px-6 py-3 text-center">Support Eng.</th>
                  <th className="px-6 py-3 text-center">Viewer</th>
                </tr>
              </thead>
              <tbody>
                {permissions.map((row) => (
                  <tr key={row.permission} className="border-b last:border-0 hover:bg-muted/30">
                    <td className="px-6 py-3">{row.permission}</td>
                    {(['owner', 'admin', 'network', 'support', 'viewer'] as const).map((col) => (
                      <td key={col} className="px-6 py-3 text-center">
                        {row[col] ? <span className="text-green-500">✓</span> : <span className="text-muted-foreground">—</span>}
                      </td>
                    ))}
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
