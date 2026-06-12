'use client';

import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTheme } from 'next-themes';
import { PageHeader } from '@/components/shared/page-header';
import { LoadingState } from '@/components/shared/loading-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Tabs } from '@/components/ui/tabs';
import { Badge } from '@/components/ui/badge';
import { useSettings, useUpdateSettings } from '@/hooks/use-settings';
import { useAuthStore } from '@/stores/auth-store';
import { DEMO_MODE } from '@/lib/demo-mode';
import type { DemoSettings } from '@/stores/demo-store';

export default function SettingsPage() {
  const { theme, setTheme } = useTheme();
  const { user } = useAuthStore();
  const { data, isLoading } = useSettings();
  const updateSettings = useUpdateSettings();
  const [tab, setTab] = useState('general');

  const { register, handleSubmit, reset, watch } = useForm<DemoSettings>();

  useEffect(() => {
    if (data?.settings) {
      reset(data.settings as DemoSettings);
    }
  }, [data, reset]);

  const onSubmit = (formData: DemoSettings) => {
    updateSettings.mutate(formData);
  };

  if (isLoading) return <LoadingState variant="skeleton" />;

  const isOwner = DEMO_MODE || user?.role === 'OWNER';
  const requireMfa = watch('requireMfa');

  const tabs = [
    { id: 'general', label: 'General' },
    { id: 'monitoring', label: 'Monitoring' },
    { id: 'notifications', label: 'Notifications' },
    { id: 'security', label: 'Security' },
    { id: 'branding', label: 'Branding' },
    { id: 'integrations', label: 'Integrations' },
  ];

  const SaveButton = () =>
    isOwner ? (
      <Button type="submit" disabled={updateSettings.isPending}>
        Save Settings
      </Button>
    ) : null;

  return (
    <div className="space-y-6 max-w-3xl">
      <PageHeader title="Settings" description="Organization and platform configuration" />

      <Tabs tabs={tabs} active={tab} onChange={setTab} />

      <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
        {tab === 'general' && (
          <>
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
                <div className="space-y-2">
                  <Label>Status Page Slug</Label>
                  <Input {...register('statusPageSlug')} disabled={!isOwner} />
                </div>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" {...register('enablePublicStatusPage')} disabled={!isOwner} />
                  Enable public status page
                </label>
                {watch('enablePublicStatusPage') && (
                  <a href="/status" target="_blank" rel="noopener noreferrer" className="text-sm text-primary hover:underline block">
                    View /status
                  </a>
                )}
              </CardContent>
            </Card>

            <Card>
              <CardHeader><CardTitle className="text-base">Regional Settings</CardTitle></CardHeader>
              <CardContent className="space-y-4">
                <div className="space-y-2">
                  <Label>Timezone</Label>
                  <Select {...register('timezone')} disabled={!isOwner}>
                    <option value="UTC">UTC</option>
                    <option value="America/New_York">America/New_York</option>
                    <option value="Europe/London">Europe/London</option>
                    <option value="Asia/Dubai">Asia/Dubai</option>
                  </Select>
                </div>
                <SaveButton />
              </CardContent>
            </Card>

            <Card>
              <CardHeader><CardTitle className="text-base">Theme</CardTitle></CardHeader>
              <CardContent>
                <div className="flex gap-2">
                  <Button type="button" variant={theme === 'light' ? 'default' : 'outline'} onClick={() => setTheme('light')}>Light</Button>
                  <Button type="button" variant={theme === 'dark' ? 'default' : 'outline'} onClick={() => setTheme('dark')}>Dark</Button>
                  <Button type="button" variant={theme === 'system' ? 'default' : 'outline'} onClick={() => setTheme('system')}>System</Button>
                </div>
              </CardContent>
            </Card>
          </>
        )}

        {tab === 'monitoring' && (
          <Card>
            <CardHeader><CardTitle className="text-base">Monitoring Defaults</CardTitle></CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-2">
                <Label>High Latency Threshold (ms)</Label>
                <Input type="number" {...register('latencyThresholdMs', { valueAsNumber: true })} disabled={!isOwner} />
              </div>
              <div className="space-y-2">
                <Label>Default Check Interval (seconds)</Label>
                <Input type="number" {...register('defaultCheckInterval', { valueAsNumber: true })} disabled={!isOwner} />
              </div>
              <div className="space-y-2">
                <Label>Default Timeout (seconds)</Label>
                <Input type="number" {...register('defaultTimeout', { valueAsNumber: true })} disabled={!isOwner} />
              </div>
              <div className="space-y-2">
                <Label>Data Retention (days)</Label>
                <Input type="number" {...register('retentionDays', { valueAsNumber: true })} disabled={!isOwner} />
              </div>
              <SaveButton />
            </CardContent>
          </Card>
        )}

        {tab === 'notifications' && (
          <Card>
            <CardHeader><CardTitle className="text-base">Notification Settings</CardTitle></CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-2">
                <Label>Default Notification Email</Label>
                <Input type="email" {...register('notificationEmail')} disabled={!isOwner} />
              </div>
              <div className="space-y-2">
                <Label>Slack Webhook</Label>
                <Input {...register('slackWebhook')} disabled={!isOwner} />
              </div>
              <SaveButton />
            </CardContent>
          </Card>
        )}

        {tab === 'security' && (
          <Card>
            <CardHeader><CardTitle className="text-base">Security</CardTitle></CardHeader>
            <CardContent className="space-y-4">
              <label className="flex items-center justify-between gap-4">
                <div>
                  <Label>Require MFA</Label>
                  <p className="text-sm text-muted-foreground">Enforce multi-factor authentication for all users</p>
                </div>
                <Badge variant={requireMfa ? 'success' : 'muted'}>
                  {requireMfa ? 'Enabled' : 'Disabled'}
                </Badge>
              </label>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" {...register('requireMfa')} disabled={!isOwner} />
                Enable MFA requirement
              </label>
              <div className="space-y-2">
                <Label>Session Timeout (minutes)</Label>
                <Input type="number" {...register('sessionTimeoutMinutes', { valueAsNumber: true })} disabled={!isOwner} />
              </div>
              <div className="space-y-2">
                <Label>IP Allowlist</Label>
                <Input placeholder="Leave empty to allow all" {...register('ipAllowlist')} disabled={!isOwner} />
              </div>
              <SaveButton />
            </CardContent>
          </Card>
        )}

        {tab === 'branding' && (
          <Card>
            <CardHeader><CardTitle className="text-base">Branding</CardTitle></CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-2">
                <Label>Primary Color</Label>
                <div className="flex items-center gap-3">
                  <div className="h-8 w-8 rounded border" style={{ backgroundColor: watch('brandPrimaryColor') }} />
                  <Input {...register('brandPrimaryColor')} disabled={!isOwner} />
                </div>
              </div>
              <div className="space-y-2">
                <Label>Logo URL</Label>
                <Input placeholder="https://..." {...register('brandLogoUrl')} disabled={!isOwner} />
              </div>
              <SaveButton />
            </CardContent>
          </Card>
        )}

        {tab === 'integrations' && (
          <Card>
            <CardHeader><CardTitle className="text-base">Integrations</CardTitle></CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-2">
                <Label>PagerDuty Integration Key</Label>
                <Input {...register('pagerdutyKey')} disabled={!isOwner} />
              </div>
              <div className="space-y-2">
                <Label>Datadog API Key</Label>
                <Input placeholder="Not configured" {...register('datadogApiKey')} disabled={!isOwner} />
              </div>
              <SaveButton />
            </CardContent>
          </Card>
        )}
      </form>

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
