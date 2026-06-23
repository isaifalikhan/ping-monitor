'use client';

import { use, useEffect } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useForm } from 'react-hook-form';
import { ArrowLeft, Clock, TrendingUp, Activity, Tag, Server, Wrench, BellOff } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
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
import { MonitorDeviceFields } from '@/components/monitors/monitor-device-fields';
import { useMonitor, useUpdateMonitor } from '@/hooks/use-monitors';
import {
  MONITOR_TYPES,
  MONITOR_TYPE_LABELS,
  MONITOR_CATEGORIES,
  type CreateMonitorFormData,
  type MonitorCategory,
  type SupportedMonitorType,
  type CriticalityLevel,
} from '@/lib/validations/monitor';
import type { MonitorCheck } from '@/lib/types';

const criticalityVariant: Record<string, 'muted' | 'default' | 'warning' | 'danger'> = {
  LOW: 'muted',
  MEDIUM: 'default',
  HIGH: 'warning',
  CRITICAL: 'danger',
};

function hasDeviceInfo(monitor: {
  deviceLocation?: string | null;
  rackCabinet?: string | null;
  switchPort?: string | null;
  deviceOwner?: string | null;
  vendor?: string | null;
  model?: string | null;
  ipAddress?: string | null;
}) {
  return Boolean(
    monitor.deviceLocation ||
    monitor.rackCabinet ||
    monitor.switchPort ||
    monitor.deviceOwner ||
    monitor.vendor ||
    monitor.model ||
    monitor.ipAddress,
  );
}

export default function MonitorDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const router = useRouter();
  const searchParams = useSearchParams();
  const isEditing = searchParams.get('edit') === '1';

  const { data, isLoading } = useMonitor(id);
  const updateMonitor = useUpdateMonitor();

  const { register, handleSubmit, reset } = useForm<CreateMonitorFormData>({
    defaultValues: {
      name: '',
      type: 'HTTP',
      target: '',
      port: undefined,
      category: 'Other',
      criticality: 'MEDIUM',
      checkInterval: 60,
      timeout: 30,
      retryCount: 3,
      group: '',
      description: '',
    },
  });

  useEffect(() => {
    if (data?.monitor) {
      const m = data.monitor;
      reset({
        name: m.name,
        type: m.type as SupportedMonitorType,
        target: m.target,
        port: m.port ?? undefined,
        category: (m.category as MonitorCategory | null) ?? 'Other',
        criticality: (m.criticality as CriticalityLevel) ?? 'MEDIUM',
        deviceLocation: m.deviceLocation ?? undefined,
        rackCabinet: m.rackCabinet ?? undefined,
        switchPort: m.switchPort ?? undefined,
        deviceOwner: m.deviceOwner ?? undefined,
        vendor: m.vendor ?? undefined,
        model: m.model ?? undefined,
        ipAddress: m.ipAddress ?? undefined,
        snmpCommunity: m.snmpCommunity ?? undefined,
        snmpVersion: (m.snmpVersion as CreateMonitorFormData['snmpVersion']) ?? undefined,
        interfaceName: m.interfaceName ?? undefined,
        checkInterval: m.checkInterval,
        timeout: m.timeout,
        retryCount: m.retryCount,
        group: m.group ?? '',
        description: m.description ?? '',
      });
    }
  }, [data?.monitor, reset]);

  if (isLoading) return <LoadingState />;
  if (!data) return <p className="text-muted-foreground">Monitor not found</p>;

  const { monitor, checks, incidents, alertHistory, maintenanceWindows } = data;
  const typedChecks = checks as MonitorCheck[];
  const chartData = typedChecks
    .slice()
    .reverse()
    .map((c) => ({
      time: new Date(c.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
      ms: c.responseTime ?? 0,
    }));

  const onSave = (formData: CreateMonitorFormData) => {
    updateMonitor.mutate(
      { id, data: { ...formData, port: formData.port || undefined } },
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
          description={`${MONITOR_TYPE_LABELS[data.monitor.type as keyof typeof MONITOR_TYPE_LABELS] ?? monitor.type}${monitor.category ? ` · ${monitor.category}` : ''} · ${monitor.target}${monitor.port ? `:${monitor.port}` : ''}`}
        />
        <div className="ml-auto flex items-center gap-2">
          {monitor.criticality && (
            <Badge variant={criticalityVariant[monitor.criticality] ?? 'muted'}>{monitor.criticality}</Badge>
          )}
          <StatusBadge status={monitor.displayStatus ?? monitor.status} />
          {!isEditing && (
            <>
              <Button
                variant={monitor.maintenanceMode ? 'default' : 'outline'}
                size="sm"
                onClick={() =>
                  updateMonitor.mutate({
                    id,
                    data: { maintenanceMode: !monitor.maintenanceMode },
                  })
                }
                disabled={updateMonitor.isPending}
              >
                <Wrench className="h-4 w-4 mr-1" />
                {monitor.maintenanceMode ? 'Exit maintenance' : 'Maintenance mode'}
              </Button>
              <Button variant="outline" size="sm" onClick={() => router.push(`/monitors/${id}?edit=1`)}>
                Edit
              </Button>
            </>
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
              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-2">
                  <Label>Type</Label>
                  <Select {...register('type')}>
                    {MONITOR_TYPES.map((t) => (
                      <option key={t} value={t}>{MONITOR_TYPE_LABELS[t]}</option>
                    ))}
                  </Select>
                </div>
                <div className="space-y-2">
                  <Label>Category</Label>
                  <Select {...register('category')}>
                    {MONITOR_CATEGORIES.map((c) => (
                      <option key={c} value={c}>{c}</option>
                    ))}
                  </Select>
                </div>
              </div>
              <div className="space-y-2">
                <Label>Target</Label>
                <Input {...register('target')} />
              </div>
              <div className="space-y-2">
                <Label>Port (optional)</Label>
                <Input type="number" {...register('port', { valueAsNumber: true })} />
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
              <MonitorDeviceFields register={register} />
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

      {(monitor.group || monitor.tags.length > 0) && (
        <div className="flex flex-wrap items-center gap-2">
          {monitor.group && <Badge variant="outline">{monitor.group}</Badge>}
          {monitor.tags.map((t) => (
            <Badge key={t} variant="muted"><Tag className="h-3 w-3 mr-1 inline" />{t}</Badge>
          ))}
        </div>
      )}

      {!isEditing && hasDeviceInfo(monitor) && (
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-base flex items-center gap-2">
              <Server className="h-4 w-4" />
              Device information
            </CardTitle>
          </CardHeader>
          <CardContent>
            <dl className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 text-sm">
              {monitor.ipAddress && (
                <div><dt className="text-muted-foreground">Management IP</dt><dd className="font-medium">{monitor.ipAddress}</dd></div>
              )}
              {monitor.deviceLocation && (
                <div><dt className="text-muted-foreground">Location</dt><dd className="font-medium">{monitor.deviceLocation}</dd></div>
              )}
              {monitor.rackCabinet && (
                <div><dt className="text-muted-foreground">Rack / cabinet</dt><dd className="font-medium">{monitor.rackCabinet}</dd></div>
              )}
              {monitor.switchPort && (
                <div><dt className="text-muted-foreground">Switch port</dt><dd className="font-medium">{monitor.switchPort}</dd></div>
              )}
              {monitor.deviceOwner && (
                <div><dt className="text-muted-foreground">Owner</dt><dd className="font-medium">{monitor.deviceOwner}</dd></div>
              )}
              {monitor.vendor && (
                <div><dt className="text-muted-foreground">Vendor</dt><dd className="font-medium">{monitor.vendor}</dd></div>
              )}
              {monitor.model && (
                <div><dt className="text-muted-foreground">Model</dt><dd className="font-medium">{monitor.model}</dd></div>
              )}
            </dl>
          </CardContent>
        </Card>
      )}

      {!isEditing && (monitor.inMaintenance || (maintenanceWindows?.length ?? 0) > 0) && (
        <Card className={monitor.inMaintenance ? 'border-amber-500/30 bg-amber-500/5' : undefined}>
          <CardHeader className="pb-2">
            <CardTitle className="text-base flex items-center gap-2">
              <Wrench className="h-4 w-4" />
              Maintenance
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            {monitor.inMaintenance ? (
              <p>
                This monitor is in maintenance. Cron checks are skipped
                {monitor.alertsPaused ? ' and alerts are paused' : ', but alerts remain active'}.
              </p>
            ) : (
              <p className="text-muted-foreground">No active maintenance affecting this monitor.</p>
            )}
            {monitor.maintenanceWindow && (
              <p>
                Active window: <strong>{monitor.maintenanceWindow.title}</strong>
              </p>
            )}
            {monitor.maintenanceMode && (
              <Badge variant="warning">Manual maintenance mode enabled</Badge>
            )}
            {monitor.alertsPaused && (
              <Badge variant="muted" className="gap-1">
                <BellOff className="h-3 w-3" />
                Alerts paused
              </Badge>
            )}
            {(maintenanceWindows ?? []).length > 0 && (
              <ul className="text-muted-foreground space-y-1">
                {maintenanceWindows!.map((w) => (
                  <li key={w.id}>
                    {w.title} ({w.status.toLowerCase()}) — alerts {w.pauseAlerts ? 'paused' : 'active'}
                  </li>
                ))}
              </ul>
            )}
            <Link href={`/maintenance?monitorId=${id}`}>
              <Button variant="outline" size="sm">
                Schedule maintenance
              </Button>
            </Link>
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
