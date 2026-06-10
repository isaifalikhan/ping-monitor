'use client';

import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { useTheme } from 'next-themes';
import { PageHeader } from '@/components/shared/page-header';
import { LoadingState } from '@/components/shared/loading-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useSettings, useUpdateSettings } from '@/hooks/use-settings';
import { useAuthStore } from '@/stores/auth-store';
import { DEMO_MODE } from '@/lib/demo-mode';

interface SettingsForm {
  latencyThresholdMs: number;
  notificationEmail: string;
  timezone: string;
}

export default function SettingsPage() {
  const { theme, setTheme } = useTheme();
  const { user } = useAuthStore();
  const { data, isLoading } = useSettings();
  const updateSettings = useUpdateSettings();

  const { register, handleSubmit, reset } = useForm<SettingsForm>();

  useEffect(() => {
    if (data?.settings) {
      reset({
        latencyThresholdMs: data.settings.latencyThresholdMs,
        notificationEmail: data.settings.notificationEmail ?? '',
        timezone: data.settings.timezone,
      });
    }
  }, [data, reset]);

  const onSubmit = (formData: SettingsForm) => {
    updateSettings.mutate(formData);
  };

  if (isLoading) return <LoadingState />;

  const isOwner = DEMO_MODE || user?.role === 'OWNER';

  return (
    <div className="space-y-6 max-w-2xl">
      <PageHeader title="Settings" description="Organization and account preferences" />

      <Card>
        <CardHeader><CardTitle className="text-base">Organization Profile</CardTitle></CardHeader>
        <CardContent className="space-y-3">
          <div>
            <Label className="text-muted-foreground">Organization</Label>
            <p className="font-medium">{data?.organization?.name}</p>
          </div>
          <div>
            <Label className="text-muted-foreground">Slug</Label>
            <p className="font-medium">{data?.organization?.slug}</p>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle className="text-base">Threshold & Notifications</CardTitle></CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
            <div className="space-y-2">
              <Label>High Latency Threshold (ms)</Label>
              <Input type="number" {...register('latencyThresholdMs', { valueAsNumber: true })} disabled={!isOwner} />
            </div>
            <div className="space-y-2">
              <Label>Notification Email</Label>
              <Input type="email" {...register('notificationEmail')} disabled={!isOwner} />
            </div>
            <div className="space-y-2">
              <Label>Timezone</Label>
              <Select {...register('timezone')} disabled={!isOwner}>
                <option value="UTC">UTC</option>
                <option value="America/New_York">America/New_York</option>
                <option value="Europe/London">Europe/London</option>
                <option value="Asia/Dubai">Asia/Dubai</option>
              </Select>
            </div>
            {isOwner && (
              <Button type="submit" disabled={updateSettings.isPending}>Save Settings</Button>
            )}
          </form>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle className="text-base">Theme</CardTitle></CardHeader>
        <CardContent>
          <div className="flex gap-2">
            <Button variant={theme === 'light' ? 'default' : 'outline'} onClick={() => setTheme('light')}>Light</Button>
            <Button variant={theme === 'dark' ? 'default' : 'outline'} onClick={() => setTheme('dark')}>Dark</Button>
            <Button variant={theme === 'system' ? 'default' : 'outline'} onClick={() => setTheme('system')}>System</Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle className="text-base">Account</CardTitle></CardHeader>
        <CardContent className="space-y-2">
          <p className="text-sm"><span className="text-muted-foreground">Name:</span> {user?.firstName} {user?.lastName}</p>
          <p className="text-sm"><span className="text-muted-foreground">Email:</span> {user?.email}</p>
          <p className="text-sm"><span className="text-muted-foreground">Role:</span> {user?.role}</p>
        </CardContent>
      </Card>
    </div>
  );
}
