'use client';

import { useState } from 'react';
import { PageHeader } from '@/components/shared/page-header';
import { StatusBadge } from '@/components/shared/status-badge';
import { LoadingState } from '@/components/shared/loading-state';
import { EmptyState } from '@/components/shared/empty-state';
import { Button } from '@/components/ui/button';
import { Select } from '@/components/ui/select';
import { Card, CardContent } from '@/components/ui/card';
import { useIncidents, useAcknowledgeIncident, useResolveIncident } from '@/hooks/use-incidents';

function formatDuration(seconds?: number | null) {
  if (seconds == null) return '—';
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

export default function IncidentsPage() {
  const [status, setStatus] = useState('');
  const { data: incidents, isLoading } = useIncidents(status || undefined);
  const acknowledge = useAcknowledgeIncident();
  const resolve = useResolveIncident();

  return (
    <div className="space-y-6">
      <PageHeader title="Incidents" description="Track and resolve infrastructure incidents" />

      <Select value={status} onChange={(e) => setStatus(e.target.value)} className="max-w-[200px]">
        <option value="">All statuses</option>
        <option value="OPEN">Open</option>
        <option value="ACKNOWLEDGED">Acknowledged</option>
        <option value="RESOLVED">Resolved</option>
      </Select>

      {isLoading ? (
        <LoadingState />
      ) : !incidents?.length ? (
        <EmptyState title="No incidents" description="Incidents are created automatically when monitors fail." />
      ) : (
        <Card>
          <CardContent className="p-0">
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b text-left text-muted-foreground">
                    <th className="px-6 py-3 font-medium">Monitor</th>
                    <th className="px-6 py-3 font-medium">Status</th>
                    <th className="px-6 py-3 font-medium">Severity</th>
                    <th className="px-6 py-3 font-medium">Started</th>
                    <th className="px-6 py-3 font-medium">Duration</th>
                    <th className="px-6 py-3 font-medium">Assigned</th>
                    <th className="px-6 py-3 font-medium">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {incidents.map((i) => (
                    <tr key={i.id} className="border-b last:border-0 hover:bg-muted/50">
                      <td className="px-6 py-3 font-medium">{i.monitor.name}</td>
                      <td className="px-6 py-3"><StatusBadge status={i.status} /></td>
                      <td className="px-6 py-3"><StatusBadge status={i.severity} /></td>
                      <td className="px-6 py-3 text-muted-foreground">{new Date(i.startedAt).toLocaleString()}</td>
                      <td className="px-6 py-3">{formatDuration(i.duration)}</td>
                      <td className="px-6 py-3">
                        {i.assignedUser ? `${i.assignedUser.firstName} ${i.assignedUser.lastName}` : '—'}
                      </td>
                      <td className="px-6 py-3">
                        <div className="flex gap-2">
                          {i.status === 'OPEN' && (
                            <Button size="sm" variant="outline" disabled={acknowledge.isPending}
                              onClick={() => acknowledge.mutate(i.id)}>Acknowledge</Button>
                          )}
                          {i.status !== 'RESOLVED' && (
                            <Button size="sm" disabled={resolve.isPending}
                              onClick={() => resolve.mutate(i.id)}>Resolve</Button>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </CardContent>
        </Card>
      )}
    </div>
  );
}
