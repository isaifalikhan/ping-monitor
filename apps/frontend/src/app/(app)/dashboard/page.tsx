'use client';

import {
  Monitor,
  CheckCircle2,
  XCircle,
  AlertTriangle,
  Activity,
  Clock,
  TrendingUp,
} from 'lucide-react';
import { StatCard } from '@/components/dashboard/stat-card';
import {
  ResponseTimeChart,
  UptimeChart,
  Uptime24hChart,
  IncidentChart,
  MonitorDistributionChart,
  StatusDistributionChart,
} from '@/components/dashboard/charts';
import {
  RecentAlertsTable,
  RecentIncidentsTable,
  RecentChecksTable,
} from '@/components/dashboard/recent-tables';
import { useAuthStore } from '@/stores/auth-store';
import { useDashboardStats } from '@/hooks/use-dashboard';

export default function DashboardPage() {
  const { user } = useAuthStore();
  const { stats } = useDashboardStats();

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">Dashboard</h1>
        <p className="text-muted-foreground">
          Welcome back, {user?.firstName}. Here&apos;s your infrastructure overview.
        </p>
      </div>

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-7">
        <StatCard title="Total Monitors" value={stats.totalMonitors} icon={Monitor} />
        <StatCard title="Online" value={stats.online} icon={CheckCircle2} variant="success" />
        <StatCard title="Offline" value={stats.offline} icon={XCircle} variant="danger" />
        <StatCard title="Degraded" value={stats.degraded} icon={AlertTriangle} variant="warning" />
        <StatCard title="Active Incidents" value={stats.activeIncidents} icon={Activity} variant="danger" />
        <StatCard title="Uptime" value={`${stats.uptimePercent}%`} icon={TrendingUp} variant="success" trend="↑ 0.02%" subtitle="last 30 days" />
        <StatCard title="Avg Response" value={`${stats.avgResponseTime}ms`} icon={Clock} subtitle="last 24 hours" />
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <ResponseTimeChart />
        <Uptime24hChart />
        <UptimeChart />
      </div>

      <div className="grid gap-4 lg:grid-cols-2 xl:grid-cols-3">
        <IncidentChart />
        <MonitorDistributionChart />
        <StatusDistributionChart />
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <RecentIncidentsTable />
        <RecentAlertsTable />
      </div>

      <RecentChecksTable />
    </div>
  );
}
