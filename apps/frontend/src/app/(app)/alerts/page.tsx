'use client';

import { useState } from 'react';
import { Plus, Zap } from 'lucide-react';
import { PageHeader } from '@/components/shared/page-header';
import { StatusBadge } from '@/components/shared/status-badge';
import { LoadingState } from '@/components/shared/loading-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import {
  useAlertChannels,
  useAlertRules,
  useCreateAlertChannel,
  useCreateAlertRule,
  useTestAlert,
} from '@/hooks/use-alerts';
import { useMonitors } from '@/hooks/use-monitors';

const CHANNELS = ['EMAIL', 'WHATSAPP', 'TELEGRAM', 'SLACK', 'TEAMS', 'WEBHOOK'] as const;
const TRIGGERS = ['MONITOR_DOWN', 'MONITOR_RECOVERY', 'HIGH_LATENCY', 'PACKET_LOSS', 'SSL_EXPIRY', 'DNS_FAILURE'] as const;

export default function AlertsPage() {
  const { data: channels, isLoading: loadingChannels } = useAlertChannels();
  const { data: rules, isLoading: loadingRules } = useAlertRules();
  const { data: monitors } = useMonitors();
  const createChannel = useCreateAlertChannel();
  const createRule = useCreateAlertRule();
  const testAlert = useTestAlert();

  const [showChannelForm, setShowChannelForm] = useState(false);
  const [showRuleForm, setShowRuleForm] = useState(false);
  const [channelType, setChannelType] = useState<string>('EMAIL');
  const [channelName, setChannelName] = useState('');
  const [channelEmail, setChannelEmail] = useState('');
  const [ruleChannelId, setRuleChannelId] = useState('');
  const [ruleTrigger, setRuleTrigger] = useState<string>('MONITOR_DOWN');
  const [ruleMonitorId, setRuleMonitorId] = useState('');

  const handleCreateChannel = () => {
    const config = channelType === 'EMAIL' ? { email: channelEmail } : { url: channelEmail };
    createChannel.mutate(
      { channel: channelType, name: channelName, config },
      {
        onSuccess: () => {
          setShowChannelForm(false);
          setChannelName('');
          setChannelEmail('');
        },
      },
    );
  };

  const handleCreateRule = () => {
    createRule.mutate(
      {
        channelId: ruleChannelId,
        trigger: ruleTrigger,
        ...(ruleMonitorId && { monitorId: ruleMonitorId }),
      },
      { onSuccess: () => setShowRuleForm(false) },
    );
  };

  if (loadingChannels || loadingRules) return <LoadingState />;

  return (
    <div className="space-y-6">
      <PageHeader title="Alerts" description="Configure notification channels and alert rules" />

      <div className="flex gap-3">
        <Button onClick={() => setShowChannelForm(!showChannelForm)}>
          <Plus className="h-4 w-4 mr-2" />Add Channel
        </Button>
        <Button variant="outline" onClick={() => setShowRuleForm(!showRuleForm)}>
          <Plus className="h-4 w-4 mr-2" />Add Rule
        </Button>
      </div>

      {showChannelForm && (
        <Card>
          <CardHeader><CardTitle className="text-base">New Alert Channel</CardTitle></CardHeader>
          <CardContent className="space-y-4 max-w-md">
            <div className="space-y-2">
              <Label>Name</Label>
              <Input value={channelName} onChange={(e) => setChannelName(e.target.value)} placeholder="Ops Email" />
            </div>
            <div className="space-y-2">
              <Label>Channel Type</Label>
              <Select value={channelType} onChange={(e) => setChannelType(e.target.value)}>
                {CHANNELS.map((c) => <option key={c} value={c}>{c}</option>)}
              </Select>
            </div>
            <div className="space-y-2">
              <Label>{channelType === 'EMAIL' ? 'Email Address' : 'Webhook / Config'}</Label>
              <Input value={channelEmail} onChange={(e) => setChannelEmail(e.target.value)} placeholder={channelType === 'EMAIL' ? 'ops@company.com' : 'https://...'} />
            </div>
            <Button onClick={handleCreateChannel} disabled={createChannel.isPending || !channelName}>Save Channel</Button>
          </CardContent>
        </Card>
      )}

      {showRuleForm && (
        <Card>
          <CardHeader><CardTitle className="text-base">New Alert Rule</CardTitle></CardHeader>
          <CardContent className="space-y-4 max-w-md">
            <div className="space-y-2">
              <Label>Channel</Label>
              <Select value={ruleChannelId} onChange={(e) => setRuleChannelId(e.target.value)}>
                <option value="">Select channel</option>
                {channels?.map((c) => <option key={c.id} value={c.id}>{c.name} ({c.channel})</option>)}
              </Select>
            </div>
            <div className="space-y-2">
              <Label>Trigger</Label>
              <Select value={ruleTrigger} onChange={(e) => setRuleTrigger(e.target.value)}>
                {TRIGGERS.map((t) => <option key={t} value={t}>{t.replace(/_/g, ' ')}</option>)}
              </Select>
            </div>
            <div className="space-y-2">
              <Label>Monitor (optional)</Label>
              <Select value={ruleMonitorId} onChange={(e) => setRuleMonitorId(e.target.value)}>
                <option value="">All monitors</option>
                {monitors?.items.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
              </Select>
            </div>
            <Button onClick={handleCreateRule} disabled={createRule.isPending || !ruleChannelId}>Save Rule</Button>
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader><CardTitle className="text-base">Alert Channels</CardTitle></CardHeader>
        <CardContent className="p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="px-6 py-3 text-left">Name</th>
                <th className="px-6 py-3 text-left">Type</th>
                <th className="px-6 py-3 text-left">Rules</th>
                <th className="px-6 py-3 text-left">Status</th>
                <th className="px-6 py-3 text-left">Actions</th>
              </tr>
            </thead>
            <tbody>
              {!channels?.length ? (
                <tr><td colSpan={5} className="px-6 py-4 text-center text-muted-foreground">No channels configured</td></tr>
              ) : channels.map((c) => (
                <tr key={c.id} className="border-b last:border-0">
                  <td className="px-6 py-3 font-medium">{c.name}</td>
                  <td className="px-6 py-3">{c.channel}</td>
                  <td className="px-6 py-3">{c.rulesCount ?? 0}</td>
                  <td className="px-6 py-3"><StatusBadge status={c.isActive ? 'UP' : 'DOWN'} /></td>
                  <td className="px-6 py-3">
                    <Button size="sm" variant="outline" onClick={() => testAlert.mutate(c.id)} disabled={testAlert.isPending}>
                      <Zap className="h-3 w-3 mr-1" />Test
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle className="text-base">Alert Rules</CardTitle></CardHeader>
        <CardContent className="p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="px-6 py-3 text-left">Trigger</th>
                <th className="px-6 py-3 text-left">Channel</th>
                <th className="px-6 py-3 text-left">Monitor</th>
                <th className="px-6 py-3 text-left">Active</th>
              </tr>
            </thead>
            <tbody>
              {!rules?.length ? (
                <tr><td colSpan={4} className="px-6 py-4 text-center text-muted-foreground">No rules configured</td></tr>
              ) : rules.map((r) => (
                <tr key={r.id} className="border-b last:border-0">
                  <td className="px-6 py-3">{r.trigger.replace(/_/g, ' ')}</td>
                  <td className="px-6 py-3">{r.channel.name}</td>
                  <td className="px-6 py-3">{r.monitor?.name ?? 'All monitors'}</td>
                  <td className="px-6 py-3"><StatusBadge status={r.isActive ? 'UP' : 'DOWN'} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </CardContent>
      </Card>
    </div>
  );
}
