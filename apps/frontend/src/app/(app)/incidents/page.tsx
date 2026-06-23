'use client';

import { useState } from 'react';
import { ChevronDown, ChevronRight, Clock, User } from 'lucide-react';
import { PageHeader } from '@/components/shared/page-header';
import { StatusBadge } from '@/components/shared/status-badge';
import { LoadingState } from '@/components/shared/loading-state';
import { EmptyState } from '@/components/shared/empty-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { Select } from '@/components/ui/select';
import { Card, CardContent } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import {
  useIncidents,
  useAcknowledgeIncident,
  useResolveIncident,
  useUpdateIncident,
} from '@/hooks/use-incidents';
import { useTeam } from '@/hooks/use-team';
import { useAuthStore } from '@/stores/auth-store';
import { canManageIncidents } from '@/lib/roles';
import type { Incident, IncidentTimelineEvent } from '@/lib/types';

const timelineLabel: Record<string, string> = {
  DETECTED: 'Detected',
  ACKNOWLEDGED: 'Acknowledged',
  ASSIGNED: 'Assigned',
  ALERT_SENT: 'Alert sent',
  NOTE: 'Note',
  ESCALATED: 'Escalated',
  RESOLVED: 'Resolved',
};

function formatDuration(seconds?: number | null) {
  if (seconds == null) return '—';
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

function IncidentRow({
  incident,
  canWrite,
  engineers,
  onAcknowledge,
  onResolve,
  onAssign,
  onAddNote,
  ackPending,
  resolvePending,
  updatePending,
}: {
  incident: Incident;
  canWrite: boolean;
  engineers: { id: string; firstName: string; lastName: string }[];
  onAcknowledge: () => void;
  onResolve: (data: { rootCause?: string; notes?: string }) => void;
  onAssign: (userId: string) => void;
  onAddNote: (note: string) => void;
  ackPending: boolean;
  resolvePending: boolean;
  updatePending: boolean;
}) {
  const [expanded, setExpanded] = useState(false);
  const [showResolve, setShowResolve] = useState(false);
  const [rootCause, setRootCause] = useState(incident.rootCause ?? '');
  const [notes, setNotes] = useState(incident.notes ?? '');
  const [noteText, setNoteText] = useState('');

  const handleResolve = () => {
    onResolve({
      rootCause: rootCause.trim() || undefined,
      notes: notes.trim() || undefined,
    });
    setShowResolve(false);
  };

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
          {canWrite && (
            <div className="flex gap-2">
              {incident.status === 'OPEN' && (
                <Button size="sm" variant="outline" disabled={ackPending} onClick={onAcknowledge}>
                  Acknowledge
                </Button>
              )}
              {incident.status !== 'RESOLVED' && (
                <Button size="sm" disabled={resolvePending} onClick={() => setShowResolve(true)}>
                  Resolve
                </Button>
              )}
            </div>
          )}
        </td>
      </tr>
      {expanded && (
        <tr className="border-b bg-muted/20">
          <td colSpan={7} className="px-6 py-4">
            <div className="grid gap-4 lg:grid-cols-2">
              <div className="space-y-3">
                <h4 className="text-sm font-semibold">Timeline</h4>
                <div className="space-y-2">
                  {(incident.timeline ?? []).length === 0 ? (
                    <p className="text-sm text-muted-foreground">No timeline events yet.</p>
                  ) : (incident.timeline ?? []).map((event: IncidentTimelineEvent) => (
                    <div key={event.id} className="flex gap-3 text-sm">
                      <div className="mt-1.5 h-2 w-2 rounded-full bg-primary shrink-0" />
                      <div>
                        <div className="flex items-center gap-2 flex-wrap">
                          <Badge variant="outline" className="text-[10px]">
                            {timelineLabel[event.type] ?? event.type}
                          </Badge>
                          <p>{event.message}</p>
                        </div>
                        <p className="text-xs text-muted-foreground">
                          {event.user && `${event.user} · `}
                          {new Date(event.createdAt).toLocaleString()}
                        </p>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
              <div className="space-y-4">
                {canWrite && incident.status !== 'RESOLVED' && (
                  <>
                    <div className="space-y-2">
                      <Label>Assign engineer</Label>
                      <Select
                        value={incident.assignedUser?.id ?? ''}
                        onChange={(e) => e.target.value && onAssign(e.target.value)}
                        disabled={updatePending}
                      >
                        <option value="">Unassigned</option>
                        {engineers.map((m) => (
                          <option key={m.id} value={m.id}>
                            {m.firstName} {m.lastName}
                          </option>
                        ))}
                      </Select>
                    </div>
                    <div className="space-y-2">
                      <Label>Add note</Label>
                      <div className="flex gap-2">
                        <Input
                          value={noteText}
                          onChange={(e) => setNoteText(e.target.value)}
                          placeholder="Investigation update..."
                        />
                        <Button
                          type="button"
                          variant="outline"
                          disabled={!noteText.trim() || updatePending}
                          onClick={() => {
                            onAddNote(noteText.trim());
                            setNoteText('');
                          }}
                        >
                          Add
                        </Button>
                      </div>
                    </div>
                  </>
                )}
                {incident.rootCause && (
                  <div>
                    <h4 className="text-sm font-semibold mb-1">Root cause</h4>
                    <p className="text-sm text-muted-foreground">{incident.rootCause}</p>
                  </div>
                )}
                {incident.notes && (
                  <div>
                    <h4 className="text-sm font-semibold mb-1">Resolution notes</h4>
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
            {showResolve && (
              <div className="mt-4 p-4 border rounded-lg space-y-3 bg-background">
                <h4 className="text-sm font-semibold">Resolve incident</h4>
                <div className="space-y-2">
                  <Label>Root cause</Label>
                  <Textarea value={rootCause} onChange={(e) => setRootCause(e.target.value)} rows={2} />
                </div>
                <div className="space-y-2">
                  <Label>Resolution notes</Label>
                  <Textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={2} />
                </div>
                <div className="flex gap-2">
                  <Button size="sm" disabled={resolvePending} onClick={handleResolve}>
                    Confirm resolve
                  </Button>
                  <Button size="sm" variant="outline" onClick={() => setShowResolve(false)}>
                    Cancel
                  </Button>
                </div>
              </div>
            )}
          </td>
        </tr>
      )}
    </>
  );
}

export default function IncidentsPage() {
  const [status, setStatus] = useState('');
  const role = useAuthStore((s) => s.user?.role);
  const canWrite = canManageIncidents(role);

  const { data: incidents, isLoading } = useIncidents(status || undefined);
  const { data: team } = useTeam();
  const acknowledge = useAcknowledgeIncident();
  const resolve = useResolveIncident();
  const updateIncident = useUpdateIncident();

  const engineers = (team?.members ?? []).filter(
    (m) => m.role === 'ENGINEER' || m.role === 'ADMIN' || m.role === 'OWNER',
  );

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
                      canWrite={canWrite}
                      engineers={engineers}
                      onAcknowledge={() => acknowledge.mutate(i.id)}
                      onResolve={(data) => resolve.mutate({ id: i.id, ...data })}
                      onAssign={(userId) => updateIncident.mutate({ id: i.id, data: { assignedUserId: userId } })}
                      onAddNote={(note) => updateIncident.mutate({ id: i.id, data: { note } })}
                      ackPending={acknowledge.isPending}
                      resolvePending={resolve.isPending}
                      updatePending={updateIncident.isPending}
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
