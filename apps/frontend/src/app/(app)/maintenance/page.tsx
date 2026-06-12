'use client';

import { useState } from 'react';
import { Plus, Wrench, Clock, CheckCircle2 } from 'lucide-react';
import { PageHeader } from '@/components/shared/page-header';
import { LoadingState } from '@/components/shared/loading-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Tabs } from '@/components/ui/tabs';
import { useMaintenanceWindows, useCreateMaintenance } from '@/hooks/use-maintenance';
import { useMonitors } from '@/hooks/use-monitors';
import type { MaintenanceWindow } from '@/lib/types';

const statusBadge: Record<string, 'warning' | 'success' | 'muted' | 'default'> = {
  UPCOMING: 'default',
  ACTIVE: 'warning',
  COMPLETED: 'success',
};

function MaintenanceTable({ windows }: { windows: MaintenanceWindow[] }) {
  if (!windows.length) {
    return <p className="px-6 py-8 text-center text-muted-foreground text-sm">No maintenance windows in this category.</p>;
  }

  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="border-b text-muted-foreground">
          <th className="px-6 py-3 text-left">Title</th>
          <th className="px-6 py-3 text-left">Monitor</th>
          <th className="px-6 py-3 text-left">Start</th>
          <th className="px-6 py-3 text-left">End</th>
          <th className="px-6 py-3 text-left">Status</th>
          <th className="px-6 py-3 text-left">Alerts</th>
        </tr>
      </thead>
      <tbody>
        {windows.map((w) => (
          <tr key={w.id} className="border-b last:border-0 hover:bg-muted/50 transition-colors">
            <td className="px-6 py-3 font-medium">{w.title}</td>
            <td className="px-6 py-3">{w.monitor?.name ?? 'All monitors'}</td>
            <td className="px-6 py-3 text-muted-foreground">{new Date(w.startTime).toLocaleString()}</td>
            <td className="px-6 py-3 text-muted-foreground">{new Date(w.endTime).toLocaleString()}</td>
            <td className="px-6 py-3">
              <Badge variant={statusBadge[w.status] ?? 'muted'}>{w.status}</Badge>
            </td>
            <td className="px-6 py-3">
              <Badge variant={w.pauseAlerts ? 'warning' : 'muted'}>
                {w.pauseAlerts ? 'Paused' : 'Active'}
              </Badge>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function TimelineView({ windows }: { windows: MaintenanceWindow[] }) {
  const sorted = [...windows].sort((a, b) => new Date(a.startTime).getTime() - new Date(b.startTime).getTime());

  return (
    <div className="px-6 py-4 space-y-4">
      {sorted.map((w) => (
        <div key={w.id} className="flex gap-4">
          <div className="flex flex-col items-center">
            <div className={`h-3 w-3 rounded-full ${w.status === 'ACTIVE' ? 'bg-amber-500' : w.status === 'COMPLETED' ? 'bg-green-500' : 'bg-primary'}`} />
            <div className="w-px flex-1 bg-border min-h-[40px]" />
          </div>
          <div className="pb-4 flex-1">
            <div className="flex items-center gap-2">
              <p className="font-medium">{w.title}</p>
              <Badge variant={statusBadge[w.status] ?? 'muted'} className="text-xs">{w.status}</Badge>
            </div>
            <p className="text-sm text-muted-foreground mt-1">
              {w.monitor?.name ?? 'All monitors'} · {new Date(w.startTime).toLocaleString()} — {new Date(w.endTime).toLocaleString()}
            </p>
            {w.reason && <p className="text-sm text-muted-foreground mt-1">{w.reason}</p>}
          </div>
        </div>
      ))}
    </div>
  );
}

export default function MaintenancePage() {
  const { data: windows, isLoading } = useMaintenanceWindows();
  const { data: monitors } = useMonitors();
  const createMaintenance = useCreateMaintenance();

  const [tab, setTab] = useState('all');
  const [showForm, setShowForm] = useState(false);
  const [title, setTitle] = useState('');
  const [monitorId, setMonitorId] = useState('');
  const [startTime, setStartTime] = useState('');
  const [endTime, setEndTime] = useState('');
  const [reason, setReason] = useState('');
  const [pauseAlerts, setPauseAlerts] = useState(true);

  const upcoming = windows?.filter((w) => w.status === 'UPCOMING') ?? [];
  const active = windows?.filter((w) => w.status === 'ACTIVE') ?? [];
  const completed = windows?.filter((w) => w.status === 'COMPLETED') ?? [];

  const handleSubmit = () => {
    createMaintenance.mutate(
      {
        title,
        monitorId: monitorId || undefined,
        startTime: new Date(startTime).toISOString(),
        endTime: new Date(endTime).toISOString(),
        reason,
        pauseAlerts,
      },
      {
        onSuccess: () => {
          setShowForm(false);
          setTitle('');
          setMonitorId('');
          setStartTime('');
          setEndTime('');
          setReason('');
        },
      },
    );
  };

  if (isLoading) return <LoadingState variant="skeleton" />;

  const tabs = [
    { id: 'all', label: 'All', count: windows?.length },
    { id: 'upcoming', label: 'Upcoming', count: upcoming.length },
    { id: 'active', label: 'Active', count: active.length },
    { id: 'completed', label: 'Completed', count: completed.length },
    { id: 'timeline', label: 'Timeline' },
  ];

  const filtered =
    tab === 'upcoming' ? upcoming :
    tab === 'active' ? active :
    tab === 'completed' ? completed :
    windows ?? [];

  return (
    <div className="space-y-6">
      <PageHeader
        title="Maintenance"
        description="Schedule maintenance windows and pause alerts"
        actionButton={
          <Button onClick={() => setShowForm(!showForm)}>
            <Plus className="h-4 w-4 mr-2" />Schedule Maintenance
          </Button>
        }
      />

      <div className="grid gap-4 sm:grid-cols-3">
        <Card>
          <CardContent className="flex items-center gap-3 p-4">
            <Clock className="h-8 w-8 text-primary" />
            <div>
              <p className="text-2xl font-bold">{upcoming.length}</p>
              <p className="text-sm text-muted-foreground">Upcoming</p>
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="flex items-center gap-3 p-4">
            <Wrench className="h-8 w-8 text-amber-500" />
            <div>
              <p className="text-2xl font-bold">{active.length}</p>
              <p className="text-sm text-muted-foreground">Active Now</p>
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="flex items-center gap-3 p-4">
            <CheckCircle2 className="h-8 w-8 text-green-500" />
            <div>
              <p className="text-2xl font-bold">{completed.length}</p>
              <p className="text-sm text-muted-foreground">Completed</p>
            </div>
          </CardContent>
        </Card>
      </div>

      {showForm && (
        <Card>
          <CardHeader><CardTitle className="text-base">New Maintenance Window</CardTitle></CardHeader>
          <CardContent className="space-y-4 max-w-lg">
            <div className="space-y-2">
              <Label>Title</Label>
              <Input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Database upgrade" />
            </div>
            <div className="space-y-2">
              <Label>Monitor (optional)</Label>
              <Select value={monitorId} onChange={(e) => setMonitorId(e.target.value)}>
                <option value="">All monitors</option>
                {monitors?.items.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
              </Select>
            </div>
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-2">
                <Label>Start</Label>
                <Input type="datetime-local" value={startTime} onChange={(e) => setStartTime(e.target.value)} />
              </div>
              <div className="space-y-2">
                <Label>End</Label>
                <Input type="datetime-local" value={endTime} onChange={(e) => setEndTime(e.target.value)} />
              </div>
            </div>
            <div className="space-y-2">
              <Label>Reason</Label>
              <Input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Scheduled maintenance" />
            </div>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={pauseAlerts} onChange={(e) => setPauseAlerts(e.target.checked)} />
              Pause alerts during maintenance
            </label>
            <Button onClick={handleSubmit} disabled={createMaintenance.isPending || !title || !startTime || !endTime}>
              Create Window
            </Button>
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader className="pb-0">
          <Tabs tabs={tabs} active={tab} onChange={setTab} />
        </CardHeader>
        <CardContent className="p-0">
          {tab === 'timeline' ? (
            <TimelineView windows={windows ?? []} />
          ) : (
            <MaintenanceTable windows={filtered} />
          )}
        </CardContent>
      </Card>
    </div>
  );
}
