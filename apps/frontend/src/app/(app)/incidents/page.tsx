'use client';

import { useState } from 'react';
import { ChevronDown, ChevronRight, Clock, User } from 'lucide-react';
import { PageHeader } from '@/components/shared/page-header';
import { StatusBadge } from '@/components/shared/status-badge';
import { LoadingState } from '@/components/shared/loading-state';
import { EmptyState } from '@/components/shared/empty-state';
import { Button } from '@/components/ui/button';
import { Select } from '@/components/ui/select';
import { Card, CardContent } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { useIncidents, useAcknowledgeIncident, useResolveIncident } from '@/hooks/use-incidents';
import type { Incident } from '@/lib/types';

function formatDuration(seconds?: number | null) {
  if (seconds == null) return '—';
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

function IncidentRow({ incident, onAcknowledge, onResolve, ackPending, resolvePending }: {
  incident: Incident;
  onAcknowledge: () => void;
  onResolve: () => void;
  ackPending: boolean;
  resolvePending: boolean;
}) {
  const [expanded, setExpanded] = useState(false);

  return (
    <>
      <tr className="border-b hover:bg-muted/50 transition-colors cursor-pointer" onClick={() => setExpanded(!expanded)}>
        <td className="px-6 py-3">
          <div className="flex items-center gap-2">
            {expanded ? <ChevronDown className="h-4 w-4 text-muted-foreground" /> : <ChevronRight className="h-4 w-4 text-muted-foreground" />}
            <div>
              <p className="font-medium">{incident.title ?? incident.monitor.name}</p>
              <p className="text-xs text-muted-foreground">{incident.monitor.name}</p>
            </div>
          </div>
        </td>
        <td className="px-6 py-3"><StatusBadge status={incident.status} /></td>
        <td className="px-6 py-3"><StatusBadge status={incident.severity} /></td>
        <td className="px-6 py-3 text-muted-foreground">{new Date(incident.startedAt).toLocaleString()}</td>
        <td className="px-6 py-3">{formatDuration(incident.duration)}</td>
        <td className="px-6 py-3">
          {incident.assignedUser ? `${incident.assignedUser.firstName} ${incident.assignedUser.lastName}` : '—'}
        </td>
        <td className="px-6 py-3" onClick={(e) => e.stopPropagation()}>
          <div className="flex gap-2">
            {incident.status === 'OPEN' && (
              <Button size="sm" variant="outline" disabled={ackPending} onClick={onAcknowledge}>Acknowledge</Button>
            )}
            {incident.status !== 'RESOLVED' && (
              <Button size="sm" disabled={resolvePending} onClick={onResolve}>Resolve</Button>
            )}
          </div>
        </td>
      </tr>
      {expanded && (
        <tr className="border-b bg-muted/20">
          <td colSpan={7} className="px-6 py-4">
            <div className="grid gap-4 lg:grid-cols-2">
              <div className="space-y-3">
                <h4 className="text-sm font-semibold">Timeline</h4>
                <div className="space-y-2">
                  {(incident.timeline ?? []).map((event) => (
                    <div key={event.id} className="flex gap-3 text-sm">
                      <div className="mt-1.5 h-2 w-2 rounded-full bg-primary shrink-0" />
                      <div>
                        <p>{event.message}</p>
                        <p className="text-xs text-muted-foreground">
                          {event.user && `${event.user} · `}
                          {new Date(event.createdAt).toLocaleString()}
                        </p>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
              <div className="space-y-3">
                {incident.rootCause && (
                  <div>
                    <h4 className="text-sm font-semibold mb-1">Root Cause Analysis</h4>
                    <p className="text-sm text-muted-foreground">{incident.rootCause}</p>
                  </div>
                )}
                {incident.notes && (
                  <div>
                    <h4 className="text-sm font-semibold mb-1">Resolution Notes</h4>
                    <p className="text-sm text-muted-foreground">{incident.notes}</p>
                  </div>
                )}
                {incident.assignedUser && (
                  <div className="flex items-center gap-2 text-sm">
                    <User className="h-4 w-4 text-muted-foreground" />
                    <span>Assigned to {incident.assignedUser.firstName} {incident.assignedUser.lastName}</span>
                  </div>
                )}
              </div>
            </div>
          </td>
        </tr>
      )}
    </>
  );
}

export default function IncidentsPage() {
  const [status, setStatus] = useState('');
  const { data: incidents, isLoading } = useIncidents(status || undefined);
  const acknowledge = useAcknowledgeIncident();
  const resolve = useResolveIncident();

  const openCount = incidents?.filter((i) => i.status === 'OPEN').length ?? 0;
  const ackCount = incidents?.filter((i) => i.status === 'ACKNOWLEDGED').length ?? 0;

  return (
    <div className="space-y-6">
      <PageHeader title="Incidents" description="Track and resolve infrastructure incidents" />

      <div className="flex flex-wrap gap-3 items-center">
        <Select value={status} onChange={(e) => setStatus(e.target.value)} className="max-w-[200px]">
          <option value="">All statuses</option>
          <option value="OPEN">Open</option>
          <option value="ACKNOWLEDGED">Acknowledged</option>
          <option value="RESOLVED">Resolved</option>
        </Select>
        <div className="flex gap-2 ml-auto">
          <Badge variant="danger">{openCount} Open</Badge>
          <Badge variant="warning">{ackCount} Acknowledged</Badge>
        </div>
      </div>

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
                    <th className="px-6 py-3 font-medium">Incident</th>
                    <th className="px-6 py-3 font-medium">Status</th>
                    <th className="px-6 py-3 font-medium">Severity</th>
                    <th className="px-6 py-3 font-medium">Started</th>
                    <th className="px-6 py-3 font-medium"><Clock className="h-4 w-4 inline" /> Duration</th>
                    <th className="px-6 py-3 font-medium">Assigned</th>
                    <th className="px-6 py-3 font-medium">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {incidents.map((i) => (
                    <IncidentRow
                      key={i.id}
                      incident={i}
                      onAcknowledge={() => acknowledge.mutate(i.id)}
                      onResolve={() => resolve.mutate(i.id)}
                      ackPending={acknowledge.isPending}
                      resolvePending={resolve.isPending}
                    />
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
