'use client';

import { useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { PageHeader } from '@/components/shared/page-header';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import { Card, CardContent } from '@/components/ui/card';
import { useCreateMonitor } from '@/hooks/use-monitors';
import { useSettingsDefaults } from '@/hooks/use-settings';
import { MonitorDeviceFields } from '@/components/monitors/monitor-device-fields';
import {
  createMonitorSchema,
  type CreateMonitorFormData,
  MONITOR_TYPES,
  MONITOR_TYPE_LABELS,
  MONITOR_CATEGORIES,
} from '@/lib/validations/monitor';

export default function NewMonitorPage() {
  const router = useRouter();
  const createMonitor = useCreateMonitor();
  const defaults = useSettingsDefaults();

  const { register, handleSubmit, watch, reset, formState: { errors } } = useForm<CreateMonitorFormData>({
    resolver: zodResolver(createMonitorSchema),
    defaultValues: {
      checkInterval: 60,
      timeout: 30,
      retryCount: 3,
      type: 'HTTP',
      category: 'Other',
      criticality: 'MEDIUM',
    },
  });

  useEffect(() => {
    reset((current) => ({
      ...current,
      checkInterval: defaults.defaultCheckInterval,
      timeout: defaults.defaultTimeout,
    }));
  }, [defaults.defaultCheckInterval, defaults.defaultTimeout, reset]);

  const monitorType = watch('type');

  const onSubmit = (data: CreateMonitorFormData) => {
    const tags = data.tags ? data.tags.split(',').map((t) => t.trim()).filter(Boolean) : [];
    createMonitor.mutate(
      { ...data, tags, port: data.port || undefined },
      { onSuccess: (monitor) => router.push(`/monitors/${monitor.id}`) },
    );
  };

  const targetPlaceholder =
    monitorType === 'DNS'
      ? 'erp.company.internal'
      : monitorType === 'TCP' || monitorType === 'PING'
        ? '10.0.1.1'
        : monitorType === 'SSL'
          ? 'erp.company.internal'
          : 'https://erp.company.internal/health';

  const portHint =
    monitorType === 'PING' || monitorType === 'TCP'
      ? 'Default 80 if empty'
      : monitorType === 'SSL'
        ? 'Default 443 if empty'
        : 'Optional';

  return (
    <div className="space-y-6 max-w-2xl">
      <PageHeader title="Add Monitor" description="Monitor ERP systems, network devices, and internal services" />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="name">Name</Label>
              <Input id="name" {...register('name')} placeholder="ERP Web Portal" />
              {errors.name && <p className="text-sm text-destructive">{errors.name.message}</p>}
            </div>

            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-2">
                <Label htmlFor="type">Type</Label>
                <Select id="type" {...register('type')}>
                  {MONITOR_TYPES.map((t) => (
                    <option key={t} value={t}>{MONITOR_TYPE_LABELS[t]}</option>
                  ))}
                </Select>
              </div>
              <div className="space-y-2">
                <Label htmlFor="category">Category</Label>
                <Select id="category" {...register('category')}>
                  {MONITOR_CATEGORIES.map((c) => (
                    <option key={c} value={c}>{c}</option>
                  ))}
                </Select>
              </div>
            </div>

            <div className="space-y-2">
              <Label htmlFor="target">Target</Label>
              <Input id="target" {...register('target')} placeholder={targetPlaceholder} />
              {errors.target && <p className="text-sm text-destructive">{errors.target.message}</p>}
            </div>

            {monitorType !== 'DNS' && (
              <div className="space-y-2">
                <Label htmlFor="port">Port ({portHint})</Label>
                <Input id="port" type="number" {...register('port')} placeholder={monitorType === 'SSL' ? '443' : '80'} />
              </div>
            )}

            <div className="grid grid-cols-3 gap-4">
              <div className="space-y-2">
                <Label htmlFor="checkInterval">Interval (sec)</Label>
                <Input id="checkInterval" type="number" {...register('checkInterval')} />
              </div>
              <div className="space-y-2">
                <Label htmlFor="timeout">Timeout (sec)</Label>
                <Input id="timeout" type="number" {...register('timeout')} />
              </div>
              <div className="space-y-2">
                <Label htmlFor="retryCount">Retries</Label>
                <Input id="retryCount" type="number" {...register('retryCount')} />
              </div>
            </div>

            <div className="space-y-2">
              <Label htmlFor="group">Group</Label>
              <Input id="group" {...register('group')} placeholder="Production" />
            </div>

            <div className="space-y-2">
              <Label htmlFor="tags">Tags (comma-separated)</Label>
              <Input id="tags" {...register('tags')} placeholder="erp, critical" />
            </div>

            <div className="space-y-2">
              <Label htmlFor="description">Notes</Label>
              <Textarea id="description" {...register('description')} placeholder="Optional notes..." />
            </div>

            <MonitorDeviceFields register={register} />

            <div className="flex gap-3 pt-2">
              <Button type="submit" disabled={createMonitor.isPending}>
                {createMonitor.isPending ? 'Creating...' : 'Create Monitor'}
              </Button>
              <Button type="button" variant="outline" onClick={() => router.back()}>Cancel</Button>
            </div>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}
