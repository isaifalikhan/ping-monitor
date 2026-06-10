'use client';

import { use, useEffect } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useForm } from 'react-hook-form';
import { ArrowLeft, Clock, TrendingUp, Activity } from 'lucide-react';
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from 'recharts';
import { PageHeader } from '@/components/shared/page-header';
import { StatusBadge } from '@/components/shared/status-badge';
import { LoadingState } from '@/components/shared/loading-state';
import { StatCard } from '@/components/dashboard/stat-card';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import { useMonitor, useUpdateMonitor } from '@/hooks/use-monitors';
import { MONITOR_TYPES } from '@/lib/validations/monitor';
import type { MonitorCheck } from '@/lib/types';

export default function MonitorDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const router = useRouter();
  const searchParams = useSearchParams();
  const isEditing = searchParams.get('edit') === '1';

  const { data, isLoading } = useMonitor(id);
  const updateMonitor = useUpdateMonitor();

  const { register, handleSubmit, reset } = useForm({
    defaultValues: {
      name: '',
      type: 'PING',
      target: '',
      checkInterval: 60,
      timeout: 30,
      retryCount: 3,
      group: '',
      description: '',
    },
  });

  useEffect(() => {
    if (data?.monitor) {
      reset({
        name: data.monitor.name,
        type: data.monitor.type,
        target: data.monitor.target,
        checkInterval: data.monitor.checkInterval,
        timeout: data.monitor.timeout,
        retryCount: data.monitor.retryCount,
        group: data.monitor.group ?? '',
        description: data.monitor.description ?? '',
      });
    }
  }, [data?.monitor, reset]);

  if (isLoading) return <LoadingState />;
  if (!data) return <p className="text-muted-foreground">Monitor not found</p>;

  const { monitor, checks, incidents, alertHistory } = data;
  const typedChecks = checks as MonitorCheck[];
  const chartData = typedChecks
    .slice()
    .reverse()
    .map((c) => ({
      time: new Date(c.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
      ms: c.responseTime ?? 0,
    }));

  const onSave = (formData: Record<string, unknown>) => {
    updateMonitor.mutate(
      { id, data: formData },
      { onSuccess: () => router.replace(`/monitors/${id}`) },
    );
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-4">
        <Link href="/monitors">
          <Button variant="ghost" size="icon"><ArrowLeft className="h-4 w-4" /></Button>
        </Link>
        <PageHeader
          title={monitor.name}
          description={`${monitor.type} · ${monitor.target}`}
        />
        <div className="ml-auto flex items-center gap-2">
          <StatusBadge status={monitor.status} />
          {!isEditing && (
            <Button variant="outline" size="sm" onClick={() => router.push(`/monitors/${id}?edit=1`)}>
              Edit
            </Button>
          )}
        </div>
      </div>

      {isEditing && (
        <Card>
          <CardHeader><CardTitle className="text-base">Edit Monitor</CardTitle></CardHeader>
          <CardContent>
            <form onSubmit={handleSubmit(onSave)} className="space-y-4 max-w-2xl">
              <div className="space-y-2">
                <Label>Name</Label>
                <Input {...register('name')} />
              </div>
              <div className="space-y-2">
                <Label>Type</Label>
                <Select {...register('type')}>
                  {MONITOR_TYPES.map((t) => (
                    <option key={t} value={t}>{t}</option>
                  ))}
                </Select>
              </div>
              <div className="space-y-2">
                <Label>Target</Label>
                <Input {...register('target')} />
              </div>
              <div className="grid grid-cols-3 gap-4">
                <div className="space-y-2">
                  <Label>Interval (sec)</Label>
                  <Input type="number" {...register('checkInterval', { valueAsNumber: true })} />
                </div>
                <div className="space-y-2">
                  <Label>Timeout (sec)</Label>
                  <Input type="number" {...register('timeout', { valueAsNumber: true })} />
                </div>
                <div className="space-y-2">
                  <Label>Retries</Label>
                  <Input type="number" {...register('retryCount', { valueAsNumber: true })} />
                </div>
              </div>
              <div className="space-y-2">
                <Label>Group</Label>
                <Input {...register('group')} />
              </div>
              <div className="space-y-2">
                <Label>Notes</Label>
                <Textarea {...register('description')} />
              </div>
              <div className="flex gap-2">
                <Button type="submit" disabled={updateMonitor.isPending}>
                  {updateMonitor.isPending ? 'Saving...' : 'Save Changes'}
                </Button>
                <Button type="button" variant="outline" onClick={() => router.replace(`/monitors/${id}`)}>
                  Cancel
                </Button>
              </div>
            </form>
          </CardContent>
        </Card>
      )}

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <StatCard title="Uptime" value={`${monitor.uptimePercent}%`} icon={TrendingUp} variant="success" />
        <StatCard title="Avg Response" value={monitor.lastResponseTime != null ? `${monitor.lastResponseTime}ms` : '—'} icon={Clock} />
        <StatCard title="Interval" value={`${monitor.checkInterval}s`} icon={Activity} />
        <StatCard title="Last Check" value={monitor.lastCheckedAt ? new Date(monitor.lastCheckedAt).toLocaleTimeString() : 'Never'} icon={Clock} />
      </div>

      <Card>
        <CardHeader><CardTitle className="text-base">Response Time</CardTitle></CardHeader>
        <CardContent>
          {chartData.length > 0 ? (
            <ResponsiveContainer width="100%" height={240}>
              <LineChart data={chartData}>
                <CartesianGrid strokeDasharray="3 3" className="stroke-muted" />
                <XAxis dataKey="time" className="text-xs" />
                <YAxis unit="ms" className="text-xs" />
                <Tooltip />
                <Line type="monotone" dataKey="ms" stroke="hsl(var(--primary))" strokeWidth={2} dot={false} />
              </LineChart>
            </ResponsiveContainer>
          ) : (
            <p className="text-sm text-muted-foreground py-8 text-center">No check history for this monitor yet.</p>
          )}
        </CardContent>
      </Card>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader><CardTitle className="text-base">Recent Checks</CardTitle></CardHeader>
          <CardContent className="p-0">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-muted-foreground">
                  <th className="px-6 py-2 text-left">Status</th>
                  <th className="px-6 py-2 text-left">Response</th>
                  <th className="px-6 py-2 text-left">Time</th>
                </tr>
              </thead>
              <tbody>
                {typedChecks.length === 0 ? (
                  <tr><td colSpan={3} className="px-6 py-4 text-muted-foreground text-center">No checks yet</td></tr>
                ) : typedChecks.map((c) => (
                  <tr key={c.id} className="border-b last:border-0">
                    <td className="px-6 py-2"><StatusBadge status={c.status} /></td>
                    <td className="px-6 py-2">{c.responseTime != null ? `${c.responseTime}ms` : '—'}</td>
                    <td className="px-6 py-2 text-muted-foreground">{new Date(c.createdAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </CardContent>
        </Card>

        <Card>
          <CardHeader><CardTitle className="text-base">Incident History</CardTitle></CardHeader>
          <CardContent className="p-0">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-muted-foreground">
                  <th className="px-6 py-2 text-left">Status</th>
                  <th className="px-6 py-2 text-left">Severity</th>
                  <th className="px-6 py-2 text-left">Started</th>
                </tr>
              </thead>
              <tbody>
                {(incidents as { id: string; status: string; severity: string; startedAt: string }[]).length === 0 ? (
                  <tr><td colSpan={3} className="px-6 py-4 text-muted-foreground text-center">No incidents</td></tr>
                ) : (incidents as { id: string; status: string; severity: string; startedAt: string }[]).map((i) => (
                  <tr key={i.id} className="border-b last:border-0">
                    <td className="px-6 py-2"><StatusBadge status={i.status} /></td>
                    <td className="px-6 py-2"><StatusBadge status={i.severity} /></td>
                    <td className="px-6 py-2 text-muted-foreground">{new Date(i.startedAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader><CardTitle className="text-base">Alert History</CardTitle></CardHeader>
        <CardContent className="p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="px-6 py-2 text-left">Trigger</th>
                <th className="px-6 py-2 text-left">Status</th>
                <th className="px-6 py-2 text-left">Time</th>
              </tr>
            </thead>
            <tbody>
              {(alertHistory as { id: string; trigger: string; status: string; createdAt: string }[]).length === 0 ? (
                <tr><td colSpan={3} className="px-6 py-4 text-muted-foreground text-center">No alerts</td></tr>
              ) : (alertHistory as { id: string; trigger: string; status: string; createdAt: string }[]).map((a) => (
                <tr key={a.id} className="border-b last:border-0">
                  <td className="px-6 py-2">{a.trigger.replace(/_/g, ' ')}</td>
                  <td className="px-6 py-2"><StatusBadge status={a.status} /></td>
                  <td className="px-6 py-2 text-muted-foreground">{new Date(a.createdAt).toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </CardContent>
      </Card>
    </div>
  );
}
