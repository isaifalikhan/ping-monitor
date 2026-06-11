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

  const { register, handleSubmit, reset } = useForm<DemoSettings>();

  useEffect(() => {
    if (data?.settings) {
      reset(data.settings as DemoSettings);
    }
  }, [data, reset]);

  const onSubmit = (formData: DemoSettings) => {
    updateSettings.mutate({
      latencyThresholdMs: formData.latencyThresholdMs,
      notificationEmail: formData.notificationEmail,
      timezone: formData.timezone,
    });
  };

  if (isLoading) return <LoadingState />;

  const isOwner = DEMO_MODE || user?.role === 'OWNER';
  const settings = data?.settings as DemoSettings | undefined;

  const tabs = [
    { id: 'general', label: 'General' },
    { id: 'monitoring', label: 'Monitoring' },
    { id: 'notifications', label: 'Notifications' },
    { id: 'security', label: 'Security' },
    { id: 'branding', label: 'Branding' },
    { id: 'integrations', label: 'Integrations' },
  ];

  return (
    <div className="space-y-6 max-w-3xl">
      <PageHeader title="Settings" description="Organization and platform configuration" />

      <Tabs tabs={tabs} active={tab} onChange={setTab} />

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
              <div>
                <Label className="text-muted-foreground">Public Status Page</Label>
                <div className="flex items-center gap-2 mt-1">
                  <Badge variant={settings?.enablePublicStatusPage ? 'success' : 'muted'}>
                    {settings?.enablePublicStatusPage ? 'Enabled' : 'Disabled'}
                  </Badge>
                  {settings?.enablePublicStatusPage && (
                    <a href="/status" target="_blank" rel="noopener noreferrer" className="text-sm text-primary hover:underline">
                      /status/{settings.statusPageSlug}
                    </a>
                  )}
                </div>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader><CardTitle className="text-base">Regional Settings</CardTitle></CardHeader>
            <CardContent>
              <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
                <div className="space-y-2">
                  <Label>Timezone</Label>
                  <Select {...register('timezone')} disabled={!isOwner}>
                    <option value="UTC">UTC</option>
                    <option value="America/New_York">America/New_York</option>
                    <option value="Europe/London">Europe/London</option>
                    <option value="Asia/Dubai">Asia/Dubai</option>
                  </Select>
                </div>
                {isOwner && <Button type="submit" disabled={updateSettings.isPending}>Save Settings</Button>}
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
        </>
      )}

      {tab === 'monitoring' && (
        <Card>
          <CardHeader><CardTitle className="text-base">Monitoring Defaults</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <Label>High Latency Threshold (ms)</Label>
              <Input type="number" defaultValue={settings?.latencyThresholdMs} disabled={!isOwner} />
            </div>
            <div className="space-y-2">
              <Label>Default Check Interval (seconds)</Label>
              <Input type="number" defaultValue={settings?.defaultCheckInterval} disabled={!isOwner} />
            </div>
            <div className="space-y-2">
              <Label>Default Timeout (seconds)</Label>
              <Input type="number" defaultValue={settings?.defaultTimeout} disabled={!isOwner} />
            </div>
            <div className="space-y-2">
              <Label>Data Retention (days)</Label>
              <Input type="number" defaultValue={settings?.retentionDays} disabled={!isOwner} />
            </div>
            {isOwner && <Button disabled={updateSettings.isPending}>Save Monitoring Settings</Button>}
          </CardContent>
        </Card>
      )}

      {tab === 'notifications' && (
        <Card>
          <CardHeader><CardTitle className="text-base">Notification Settings</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <Label>Default Notification Email</Label>
              <Input type="email" defaultValue={settings?.notificationEmail} disabled={!isOwner} />
            </div>
            <div className="space-y-2">
              <Label>Slack Webhook</Label>
              <Input defaultValue={settings?.slackWebhook} disabled={!isOwner} />
            </div>
            {isOwner && <Button disabled={updateSettings.isPending}>Save Notification Settings</Button>}
          </CardContent>
        </Card>
      )}

      {tab === 'security' && (
        <Card>
          <CardHeader><CardTitle className="text-base">Security</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <div className="flex items-center justify-between">
              <div>
                <Label>Require MFA</Label>
                <p className="text-sm text-muted-foreground">Enforce multi-factor authentication for all users</p>
              </div>
              <Badge variant={settings?.requireMfa ? 'success' : 'muted'}>
                {settings?.requireMfa ? 'Enabled' : 'Disabled'}
              </Badge>
            </div>
            <div className="space-y-2">
              <Label>Session Timeout (minutes)</Label>
              <Input type="number" defaultValue={settings?.sessionTimeoutMinutes} disabled={!isOwner} />
            </div>
            <div className="space-y-2">
              <Label>IP Allowlist</Label>
              <Input placeholder="Leave empty to allow all" defaultValue={settings?.ipAllowlist} disabled={!isOwner} />
            </div>
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
                <div className="h-8 w-8 rounded border" style={{ backgroundColor: settings?.brandPrimaryColor }} />
                <Input defaultValue={settings?.brandPrimaryColor} disabled={!isOwner} />
              </div>
            </div>
            <div className="space-y-2">
              <Label>Logo URL</Label>
              <Input placeholder="https://..." defaultValue={settings?.brandLogoUrl} disabled={!isOwner} />
            </div>
          </CardContent>
        </Card>
      )}

      {tab === 'integrations' && (
        <Card>
          <CardHeader><CardTitle className="text-base">Integrations</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <Label>PagerDuty Integration Key</Label>
              <Input defaultValue={settings?.pagerdutyKey} disabled={!isOwner} />
            </div>
            <div className="space-y-2">
              <Label>Datadog API Key</Label>
              <Input placeholder="Not configured" defaultValue={settings?.datadogApiKey} disabled={!isOwner} />
            </div>
            <div className="space-y-2">
              <Label>Slack Webhook</Label>
              <Input defaultValue={settings?.slackWebhook} disabled={!isOwner} />
            </div>
          </CardContent>
        </Card>
      )}

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
