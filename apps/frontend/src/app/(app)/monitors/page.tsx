'use client';

import { useState, useEffect } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { Eye, Pencil, Pause, Trash2, Plus, Wrench } from 'lucide-react';
import { PageHeader } from '@/components/shared/page-header';
import { StatusBadge } from '@/components/shared/status-badge';
import { LoadingState } from '@/components/shared/loading-state';
import { EmptyState } from '@/components/shared/empty-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Card, CardContent } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { useMonitors, useUpdateMonitor, useDeleteMonitor } from '@/hooks/use-monitors';
import { MonitorMaintenanceBadge } from '@/components/maintenance/monitor-maintenance-badge';
import { useMaintenanceWindows } from '@/hooks/use-maintenance';
import { MONITOR_TYPES, MONITOR_TYPE_LABELS } from '@/lib/validations/monitor';

const criticalityVariant: Record<string, 'muted' | 'default' | 'warning' | 'danger'> = {
  LOW: 'muted',
  MEDIUM: 'default',
  HIGH: 'warning',
  CRITICAL: 'danger',
};

export default function MonitorsPage() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const [type, setType] = useState('');

  useEffect(() => {
    const q = searchParams.get('search');
    if (q) setSearch(q);
  }, [searchParams]);

  const { data, isLoading } = useMonitors({ search, status, type });
  const { data: maintenanceWindows } = useMaintenanceWindows();
  const updateMonitor = useUpdateMonitor();
  const deleteMonitor = useDeleteMonitor();

  const handleMaintenanceToggle = (id: string, maintenanceMode: boolean) => {
    updateMonitor.mutate({ id, data: { maintenanceMode: !maintenanceMode } });
  };

  const handlePause = (id: string, isActive: boolean) => {
    updateMonitor.mutate({ id, data: { isActive: !isActive } });
  };

  const handleDelete = (id: string) => {
    if (!confirm('Delete this monitor?')) return;
    deleteMonitor.mutate(id);
  };

  return (
    <div className="space-y-6">
      <PageHeader
        title="Monitors"
        description="Manage and monitor your infrastructure endpoints"
        actionButton={
          <Link href="/monitors/new">
            <Button>
              <Plus className="h-4 w-4 mr-2" />
              Add Monitor
            </Button>
          </Link>
        }
      />

      <div className="flex flex-col sm:flex-row gap-3">
        <Input
          placeholder="Search monitors..."
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="max-w-sm"
        />
        <Select value={status} onChange={(e) => setStatus(e.target.value)} className="max-w-[160px]">
          <option value="">All statuses</option>
          <option value="UP">Online</option>
          <option value="DOWN">Offline</option>
          <option value="DEGRADED">Degraded</option>
          <option value="UNKNOWN">Unknown</option>
          <option value="MAINTENANCE">Maintenance</option>
        </Select>
        <Select value={type} onChange={(e) => setType(e.target.value)} className="max-w-[160px]">
          <option value="">All types</option>
          {MONITOR_TYPES.map((t) => (
            <option key={t} value={t}>{MONITOR_TYPE_LABELS[t]}</option>
          ))}
        </Select>
      </div>

      {isLoading ? (
        <LoadingState />
      ) : !data?.items.length ? (
        <EmptyState
          title="No monitors yet"
          description="Create your first monitor to start tracking uptime and performance."
          action={
            <Link href="/monitors/new">
              <Button>Add Monitor</Button>
            </Link>
          }
        />
      ) : (
        <Card>
          <CardContent className="p-0">
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b text-left text-muted-foreground">
                    <th className="px-6 py-3 font-medium">Name</th>
                    <th className="px-6 py-3 font-medium">Category</th>
                    <th className="px-6 py-3 font-medium">Type</th>
                    <th className="px-6 py-3 font-medium">Status</th>
                    <th className="px-6 py-3 font-medium">Uptime</th>
                    <th className="px-6 py-3 font-medium">Response</th>
                    <th className="px-6 py-3 font-medium">Last Check</th>
                    <th className="px-6 py-3 font-medium">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((m) => (
                    <tr key={m.id} className="border-b last:border-0 hover:bg-muted/50">
                      <td className="px-6 py-3">
                        <div>
                          <p className="font-medium">
                            {m.name}
                            {!m.isActive && <span className="ml-2 text-xs text-muted-foreground">(paused)</span>}
                          </p>
                          <p className="text-xs text-muted-foreground truncate max-w-[200px]">
                            {m.ipAddress ?? m.target}
                          </p>
                          <div className="flex gap-1 mt-1 flex-wrap items-center">
                            <MonitorMaintenanceBadge monitor={m} windows={maintenanceWindows} />
                            {m.criticality && m.criticality !== 'MEDIUM' && (
                              <Badge variant={criticalityVariant[m.criticality] ?? 'muted'} className="text-[10px] px-1.5 py-0">
                                {m.criticality}
                              </Badge>
                            )}
                            {m.tags.slice(0, 2).map((t) => (
                              <Badge key={t} variant="muted" className="text-[10px] px-1.5 py-0">{t}</Badge>
                            ))}
                          </div>
                        </div>
                      </td>
                      <td className="px-6 py-3 text-muted-foreground">{m.category ?? '—'}</td>
                      <td className="px-6 py-3"><Badge variant="outline">{MONITOR_TYPE_LABELS[m.type as keyof typeof MONITOR_TYPE_LABELS] ?? m.type}</Badge></td>
                      <td className="px-6 py-3">
                        <StatusBadge status={m.displayStatus ?? m.status} />
                      </td>
                      <td className="px-6 py-3">
                        <span className={m.uptimePercent >= 99.9 ? 'text-green-500' : m.uptimePercent >= 99 ? 'text-amber-500' : 'text-red-500'}>
                          {m.uptimePercent}%
                        </span>
                      </td>
                      <td className="px-6 py-3">{m.lastResponseTime != null ? `${m.lastResponseTime}ms` : '—'}</td>
                      <td className="px-6 py-3 text-muted-foreground">
                        {m.lastCheckedAt ? new Date(m.lastCheckedAt).toLocaleString() : 'Never'}
                      </td>
                      <td className="px-6 py-3">
                        <div className="flex items-center gap-1">
                          <Button variant="ghost" size="icon" onClick={() => router.push(`/monitors/${m.id}`)}>
                            <Eye className="h-4 w-4" />
                          </Button>
                          <Button variant="ghost" size="icon" onClick={() => router.push(`/monitors/${m.id}?edit=1`)}>
                            <Pencil className="h-4 w-4" />
                          </Button>
                          <Button
                            variant="ghost"
                            size="icon"
                            title={m.maintenanceMode ? 'Exit maintenance mode' : 'Enter maintenance mode'}
                            onClick={() => handleMaintenanceToggle(m.id, m.maintenanceMode)}
                            disabled={updateMonitor.isPending}
                          >
                            <Wrench className={`h-4 w-4 ${m.inMaintenance ? 'text-amber-500' : ''}`} />
                          </Button>
                          <Button variant="ghost" size="icon" onClick={() => handlePause(m.id, m.isActive)} disabled={updateMonitor.isPending}>
                            <Pause className="h-4 w-4" />
                          </Button>
                          <Button variant="ghost" size="icon" onClick={() => handleDelete(m.id)} disabled={deleteMonitor.isPending}>
                            <Trash2 className="h-4 w-4 text-destructive" />
                          </Button>
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
