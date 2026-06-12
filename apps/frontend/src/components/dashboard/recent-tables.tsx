'use client';

import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { StatusBadge } from '@/components/shared/status-badge';
import {
  useDashboardRecentAlerts,
  useDashboardRecentIncidents,
  useDashboardRecentChecks,
} from '@/hooks/use-dashboard';

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
                    <StatusBadge status={row.status === 'sent' ? 'UP' : 'DOWN'} />
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
        <CardTitle className="text-base">Recent Monitor Activity</CardTitle>
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
