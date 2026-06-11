'use client';

import { Activity, CheckCircle2, AlertTriangle, XCircle, Wrench } from 'lucide-react';
import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { useStatusPage } from '@/hooks/use-status-page';
import { cn } from '@/lib/utils';

const statusConfig = {
  UP: { label: 'Operational', icon: CheckCircle2, color: 'text-green-500', bg: 'bg-green-500/10' },
  DEGRADED: { label: 'Degraded', icon: AlertTriangle, color: 'text-amber-500', bg: 'bg-amber-500/10' },
  DOWN: { label: 'Major Outage', icon: XCircle, color: 'text-red-500', bg: 'bg-red-500/10' },
  MAINTENANCE: { label: 'Maintenance', icon: Wrench, color: 'text-blue-500', bg: 'bg-blue-500/10' },
};

const incidentStatusColors: Record<string, string> = {
  INVESTIGATING: 'bg-red-500/10 text-red-500',
  MONITORING: 'bg-amber-500/10 text-amber-500',
  RESOLVED: 'bg-green-500/10 text-green-500',
  SCHEDULED: 'bg-blue-500/10 text-blue-500',
};

export default function StatusPage() {
  const { config, components, uptimeHistory, incidents, maintenance } = useStatusPage();
  const overall = statusConfig[config.overallStatus] ?? statusConfig.UP;
  const OverallIcon = overall.icon;

  return (
    <div className="min-h-screen bg-background">
      <header className="border-b bg-card">
        <div className="mx-auto max-w-4xl px-6 py-8">
          <div className="flex items-center gap-3 mb-6">
            <Activity className="h-8 w-8 text-primary" />
            <div>
              <h1 className="text-2xl font-bold">{config.organizationName}</h1>
              <p className="text-sm text-muted-foreground">System Status</p>
            </div>
          </div>
          <div className={cn('flex items-center gap-3 rounded-lg px-4 py-3', overall.bg)}>
            <OverallIcon className={cn('h-5 w-5', overall.color)} />
            <div>
              <p className={cn('font-semibold', overall.color)}>{overall.label}</p>
              <p className="text-sm text-muted-foreground">
                {config.overallUptime90d}% uptime over the last 90 days
              </p>
            </div>
          </div>
        </div>
      </header>

      <main className="mx-auto max-w-4xl px-6 py-8 space-y-10">
        <section>
          <h2 className="text-lg font-semibold mb-4">Services</h2>
          <div className="space-y-2">
            {components.map((c) => {
              const cfg = statusConfig[c.status];
              const Icon = cfg.icon;
              return (
                <div key={c.id} className="flex items-center justify-between rounded-lg border bg-card px-4 py-3 hover:bg-muted/30 transition-colors">
                  <div className="flex items-center gap-3">
                    <Icon className={cn('h-4 w-4', cfg.color)} />
                    <span className="font-medium">{c.name}</span>
                  </div>
                  <div className="flex items-center gap-4 text-sm">
                    <span className="text-muted-foreground">{c.uptime90d}% uptime</span>
                    <span className={cn('rounded-full px-2.5 py-0.5 text-xs font-medium', cfg.bg, cfg.color)}>
                      {cfg.label}
                    </span>
                  </div>
                </div>
              );
            })}
          </div>
        </section>

        <section>
          <h2 className="text-lg font-semibold mb-4">Uptime History (90 days)</h2>
          <div className="rounded-lg border bg-card p-4">
            <ResponsiveContainer width="100%" height={120}>
              <AreaChart data={uptimeHistory}>
                <defs>
                  <linearGradient id="spUptime" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="5%" stopColor="hsl(142 76% 36%)" stopOpacity={0.4} />
                    <stop offset="95%" stopColor="hsl(142 76% 36%)" stopOpacity={0} />
                  </linearGradient>
                </defs>
                <CartesianGrid strokeDasharray="3 3" className="stroke-muted" vertical={false} />
                <XAxis dataKey="day" hide />
                <YAxis domain={[95, 100]} hide />
                <Tooltip labelFormatter={(v) => String(v)} formatter={(v) => [`${Number(v).toFixed(2)}%`, 'Uptime']} />
                <Area type="monotone" dataKey="uptime" stroke="hsl(142 76% 36%)" fill="url(#spUptime)" strokeWidth={0} />
              </AreaChart>
            </ResponsiveContainer>
          </div>
        </section>

        <section>
          <h2 className="text-lg font-semibold mb-4">Active Incidents</h2>
          <div className="space-y-3">
            {incidents.filter((i) => i.status !== 'RESOLVED').map((inc) => (
              <div key={inc.id} className="rounded-lg border bg-card p-4">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <h3 className="font-medium">{inc.title}</h3>
                    <p className="text-sm text-muted-foreground mt-1">
                      {inc.components.join(' · ')} · {inc.impact} impact
                    </p>
                  </div>
                  <span className={cn('rounded-full px-2.5 py-0.5 text-xs font-medium shrink-0', incidentStatusColors[inc.status])}>
                    {inc.status}
                  </span>
                </div>
              </div>
            ))}
          </div>
        </section>

        <section>
          <h2 className="text-lg font-semibold mb-4">Incident History</h2>
          <div className="space-y-2">
            {incidents.filter((i) => i.status === 'RESOLVED').map((inc) => (
              <div key={inc.id} className="flex items-center justify-between rounded-lg border bg-card px-4 py-3 text-sm">
                <span className="font-medium">{inc.title}</span>
                <span className="text-muted-foreground">{new Date(inc.startedAt).toLocaleDateString()}</span>
              </div>
            ))}
          </div>
        </section>

        <section>
          <h2 className="text-lg font-semibold mb-4">Scheduled Maintenance</h2>
          <div className="space-y-2">
            {maintenance.map((m) => (
              <div key={m.id} className="flex items-center justify-between rounded-lg border bg-card px-4 py-3 text-sm">
                <div className="flex items-center gap-2">
                  <Wrench className="h-4 w-4 text-blue-500" />
                  <span className="font-medium">{m.title}</span>
                </div>
                <span className="text-muted-foreground">
                  {new Date(m.start).toLocaleDateString()} — {new Date(m.end).toLocaleDateString()}
                </span>
              </div>
            ))}
          </div>
        </section>

        <footer className="text-center text-xs text-muted-foreground pb-8">
          Powered by NetWatch · Last updated {new Date(config.lastUpdated).toLocaleString()}
        </footer>
      </main>
    </div>
  );
}
