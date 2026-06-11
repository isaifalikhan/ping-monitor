'use client';

import { useState } from 'react';
import { Shield, User, Monitor, Bell, Settings, AlertTriangle } from 'lucide-react';
import { PageHeader } from '@/components/shared/page-header';
import { Tabs } from '@/components/ui/tabs';
import { Card, CardContent } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { useAuditLogs } from '@/hooks/use-audit-logs';
import type { AuditLogEntry } from '@/lib/types';

const categoryIcons: Record<string, typeof User> = {
  USER: User,
  MONITOR: Monitor,
  ALERT: Bell,
  SECURITY: Shield,
  SETTINGS: Settings,
  INCIDENT: AlertTriangle,
};

const categoryColors: Record<string, 'default' | 'success' | 'danger' | 'warning' | 'muted'> = {
  USER: 'default',
  MONITOR: 'default',
  ALERT: 'warning',
  SECURITY: 'danger',
  SETTINGS: 'muted',
  INCIDENT: 'warning',
};

export default function AuditLogsPage() {
  const [category, setCategory] = useState('');
  const { data: logs } = useAuditLogs(category || undefined);

  const tabs = [
    { id: '', label: 'All' },
    { id: 'USER', label: 'User' },
    { id: 'MONITOR', label: 'Monitor' },
    { id: 'ALERT', label: 'Alert' },
    { id: 'INCIDENT', label: 'Incident' },
    { id: 'SECURITY', label: 'Security' },
    { id: 'SETTINGS', label: 'Settings' },
  ];

  return (
    <div className="space-y-6">
      <PageHeader
        title="Audit Logs"
        description="Complete activity trail for compliance and security review"
      />

      <Tabs tabs={tabs} active={category} onChange={setCategory} />

      <Card>
        <CardContent className="p-0">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-muted-foreground">
                  <th className="px-6 py-3 font-medium">Category</th>
                  <th className="px-6 py-3 font-medium">Action</th>
                  <th className="px-6 py-3 font-medium">Actor</th>
                  <th className="px-6 py-3 font-medium">Target</th>
                  <th className="px-6 py-3 font-medium">IP Address</th>
                  <th className="px-6 py-3 font-medium">Timestamp</th>
                </tr>
              </thead>
              <tbody>
                {logs.map((log: AuditLogEntry) => {
                  const Icon = categoryIcons[log.category] ?? User;
                  return (
                    <tr key={log.id} className="border-b last:border-0 hover:bg-muted/50 transition-colors">
                      <td className="px-6 py-3">
                        <div className="flex items-center gap-2">
                          <Icon className="h-4 w-4 text-muted-foreground" />
                          <Badge variant={categoryColors[log.category] ?? 'muted'}>{log.category}</Badge>
                        </div>
                      </td>
                      <td className="px-6 py-3 font-medium max-w-xs">{log.action}</td>
                      <td className="px-6 py-3">{log.actor}</td>
                      <td className="px-6 py-3 text-muted-foreground">{log.target ?? '—'}</td>
                      <td className="px-6 py-3 font-mono text-xs text-muted-foreground">{log.ipAddress ?? '—'}</td>
                      <td className="px-6 py-3 text-muted-foreground whitespace-nowrap">
                        {new Date(log.createdAt).toLocaleString()}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
