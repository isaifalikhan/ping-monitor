'use client';

import { useState } from 'react';
import { Plus } from 'lucide-react';
import { PageHeader } from '@/components/shared/page-header';
import { LoadingState } from '@/components/shared/loading-state';
import { EmptyState } from '@/components/shared/empty-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { useMaintenanceWindows, useCreateMaintenance } from '@/hooks/use-maintenance';
import { useMonitors } from '@/hooks/use-monitors';

export default function MaintenancePage() {
  const { data: windows, isLoading } = useMaintenanceWindows();
  const { data: monitors } = useMonitors();
  const createMaintenance = useCreateMaintenance();

  const [showForm, setShowForm] = useState(false);
  const [title, setTitle] = useState('');
  const [monitorId, setMonitorId] = useState('');
  const [startTime, setStartTime] = useState('');
  const [endTime, setEndTime] = useState('');
  const [reason, setReason] = useState('');
  const [pauseAlerts, setPauseAlerts] = useState(true);

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

  if (isLoading) return <LoadingState />;

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

      {showForm && (
        <Card>
          <CardHeader><CardTitle className="text-base">New Maintenance Window</CardTitle></CardHeader>
          <CardContent className="space-y-4 max-w-lg">
            <div className="space-y-2">
              <Label>Title</Label>
              <Input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Database upgrade" />
            </div>
            <div className="space-y-2">
              <Label>Monitor (optional — leave empty for all)</Label>
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

      {!windows?.length ? (
        <EmptyState title="No maintenance scheduled" description="Schedule a maintenance window to pause monitoring alerts." />
      ) : (
        <Card>
          <CardContent className="p-0">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-muted-foreground">
                  <th className="px-6 py-3 text-left">Title</th>
                  <th className="px-6 py-3 text-left">Monitor</th>
                  <th className="px-6 py-3 text-left">Start</th>
                  <th className="px-6 py-3 text-left">End</th>
                  <th className="px-6 py-3 text-left">Alerts</th>
                </tr>
              </thead>
              <tbody>
                {windows.map((w) => (
                  <tr key={w.id} className="border-b last:border-0">
                    <td className="px-6 py-3 font-medium">{w.title}</td>
                    <td className="px-6 py-3">{w.monitor?.name ?? 'All monitors'}</td>
                    <td className="px-6 py-3 text-muted-foreground">{new Date(w.startTime).toLocaleString()}</td>
                    <td className="px-6 py-3 text-muted-foreground">{new Date(w.endTime).toLocaleString()}</td>
                    <td className="px-6 py-3">
                      <Badge variant={w.pauseAlerts ? 'warning' : 'muted'}>
                        {w.pauseAlerts ? 'Paused' : 'Active'}
                      </Badge>
                    </td>
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
