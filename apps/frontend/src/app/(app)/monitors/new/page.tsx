'use client';

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
import { createMonitorSchema, type CreateMonitorFormData, MONITOR_TYPES } from '@/lib/validations/monitor';

export default function NewMonitorPage() {
  const router = useRouter();
  const createMonitor = useCreateMonitor();

  const { register, handleSubmit, formState: { errors } } = useForm<CreateMonitorFormData>({
    resolver: zodResolver(createMonitorSchema),
    defaultValues: { checkInterval: 60, timeout: 30, retryCount: 3, type: 'PING' },
  });

  const onSubmit = (data: CreateMonitorFormData) => {
    const tags = data.tags ? data.tags.split(',').map((t) => t.trim()).filter(Boolean) : [];
    createMonitor.mutate(
      { ...data, tags },
      { onSuccess: (monitor) => router.push(`/monitors/${monitor.id}`) },
    );
  };

  return (
    <div className="space-y-6 max-w-2xl">
      <PageHeader title="Add Monitor" description="Configure a new endpoint to monitor" />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="name">Name</Label>
              <Input id="name" {...register('name')} placeholder="Production API" />
              {errors.name && <p className="text-sm text-destructive">{errors.name.message}</p>}
            </div>

            <div className="space-y-2">
              <Label htmlFor="type">Type</Label>
              <Select id="type" {...register('type')}>
                {MONITOR_TYPES.map((t) => (
                  <option key={t} value={t}>{t}</option>
                ))}
              </Select>
            </div>

            <div className="space-y-2">
              <Label htmlFor="target">Target</Label>
              <Input id="target" {...register('target')} placeholder="https://api.example.com/health" />
              {errors.target && <p className="text-sm text-destructive">{errors.target.message}</p>}
            </div>

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
              <Input id="tags" {...register('tags')} placeholder="api, critical" />
            </div>

            <div className="space-y-2">
              <Label htmlFor="description">Notes</Label>
              <Textarea id="description" {...register('description')} placeholder="Optional notes..." />
            </div>

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
