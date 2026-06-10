'use client';

import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { cn } from '@/lib/utils';
import {
  useDashboardRecentAlerts,
  useDashboardRecentIncidents,
  useDashboardRecentChecks,
} from '@/hooks/use-dashboard';

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    UP: 'bg-green-500/10 text-green-500',
    DOWN: 'bg-red-500/10 text-red-500',
    DEGRADED: 'bg-amber-500/10 text-amber-500',
    OPEN: 'bg-red-500/10 text-red-500',
    ACKNOWLEDGED: 'bg-amber-500/10 text-amber-500',
    RESOLVED: 'bg-green-500/10 text-green-500',
    sent: 'bg-green-500/10 text-green-500',
    failed: 'bg-red-500/10 text-red-500',
  };

  return (
    <span
      className={cn(
        'inline-flex rounded-full px-2 py-0.5 text-xs font-medium',
        styles[status] || 'bg-muted text-muted-foreground',
      )}
    >
      {status}
    </span>
  );
}

export function RecentAlertsTable() {
  const recentAlerts = useDashboardRecentAlerts();

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Recent Alerts</CardTitle>
      </CardHeader>
      <CardContent className="p-0">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-muted-foreground">
                <th className="px-6 py-3 font-medium">Monitor</th>
                <th className="px-6 py-3 font-medium">Type</th>
                <th className="px-6 py-3 font-medium">Channel</th>
                <th className="px-6 py-3 font-medium">Status</th>
                <th className="px-6 py-3 font-medium">Time</th>
              </tr>
            </thead>
            <tbody>
              {recentAlerts.map((row) => (
                <tr key={row.id} className="border-b last:border-0 hover:bg-muted/50">
                  <td className="px-6 py-3 font-medium">{row.monitor}</td>
                  <td className="px-6 py-3">{row.type}</td>
                  <td className="px-6 py-3">{row.channel}</td>
                  <td className="px-6 py-3">
                    <StatusBadge status={row.status} />
                  </td>
                  <td className="px-6 py-3 text-muted-foreground">{row.time}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </CardContent>
    </Card>
  );
}

export function RecentIncidentsTable() {
  const recentIncidents = useDashboardRecentIncidents();

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Recent Incidents</CardTitle>
      </CardHeader>
      <CardContent className="p-0">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-muted-foreground">
                <th className="px-6 py-3 font-medium">Monitor</th>
                <th className="px-6 py-3 font-medium">Status</th>
                <th className="px-6 py-3 font-medium">Duration</th>
                <th className="px-6 py-3 font-medium">Started</th>
              </tr>
            </thead>
            <tbody>
              {recentIncidents.map((row) => (
                <tr key={row.id} className="border-b last:border-0 hover:bg-muted/50">
                  <td className="px-6 py-3 font-medium">{row.monitor}</td>
                  <td className="px-6 py-3">
                    <StatusBadge status={row.status} />
                  </td>
                  <td className="px-6 py-3">{row.duration}</td>
                  <td className="px-6 py-3 text-muted-foreground">{row.started}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </CardContent>
    </Card>
  );
}

export function RecentChecksTable() {
  const recentChecks = useDashboardRecentChecks();

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Recent Checks</CardTitle>
      </CardHeader>
      <CardContent className="p-0">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-muted-foreground">
                <th className="px-6 py-3 font-medium">Monitor</th>
                <th className="px-6 py-3 font-medium">Status</th>
                <th className="px-6 py-3 font-medium">Response</th>
                <th className="px-6 py-3 font-medium">Time</th>
              </tr>
            </thead>
            <tbody>
              {recentChecks.map((row) => (
                <tr key={row.id} className="border-b last:border-0 hover:bg-muted/50">
                  <td className="px-6 py-3 font-medium">{row.monitor}</td>
                  <td className="px-6 py-3">
                    <StatusBadge status={row.status} />
                  </td>
                  <td className="px-6 py-3">
                    {row.responseTime != null ? `${row.responseTime} ms` : '—'}
                  </td>
                  <td className="px-6 py-3 text-muted-foreground">{row.time}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </CardContent>
    </Card>
  );
}
