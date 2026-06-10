'use client';

import { useState } from 'react';
import { Download } from 'lucide-react';
import { BarChart, Bar, LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';
import { PageHeader } from '@/components/shared/page-header';
import { StatCard } from '@/components/dashboard/stat-card';
import { LoadingState } from '@/components/shared/loading-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useReportSummary } from '@/hooks/use-reports';
import { toast } from '@/hooks/use-toast';
import { Monitor, TrendingUp, Clock, AlertTriangle } from 'lucide-react';

export default function ReportsPage() {
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const { data, isLoading } = useReportSummary(from || undefined, to || undefined);

  const summary = data?.summary;

  const handleExport = (format: string) => {
    toast({
      title: `Export started`,
      description: `Generating ${format} report (demo simulation). Download will begin shortly.`,
      variant: 'success',
    });
  };

  if (isLoading) return <LoadingState />;

  return (
    <div className="space-y-6">
      <PageHeader title="Reports" description="Uptime, performance, and incident analytics" />

      <div className="flex flex-wrap gap-4 items-end">
        <div className="space-y-2">
          <Label>From</Label>
          <Input type="date" value={from} onChange={(e) => setFrom(e.target.value)} className="w-[160px]" />
        </div>
        <div className="space-y-2">
          <Label>To</Label>
          <Input type="date" value={to} onChange={(e) => setTo(e.target.value)} className="w-[160px]" />
        </div>
        <div className="flex gap-2 ml-auto">
          <Button variant="outline" size="sm" onClick={() => handleExport('PDF')}><Download className="h-4 w-4 mr-1" />PDF</Button>
          <Button variant="outline" size="sm" onClick={() => handleExport('CSV')}><Download className="h-4 w-4 mr-1" />CSV</Button>
          <Button variant="outline" size="sm" onClick={() => handleExport('Excel')}><Download className="h-4 w-4 mr-1" />Excel</Button>
        </div>
      </div>

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <StatCard title="Monitors" value={summary?.totalMonitors ?? 0} icon={Monitor} />
        <StatCard title="Avg Uptime" value={`${summary?.avgUptime ?? 100}%`} icon={TrendingUp} variant="success" />
        <StatCard title="Avg Response" value={`${summary?.avgResponseTime ?? 0}ms`} icon={Clock} />
        <StatCard title="Incidents" value={summary?.totalIncidents ?? 0} icon={AlertTriangle} variant="danger" />
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader><CardTitle className="text-base">Response Time Trend</CardTitle></CardHeader>
          <CardContent>
            <ResponsiveContainer width="100%" height={240}>
              <LineChart data={data?.responseTrend ?? []}>
                <CartesianGrid strokeDasharray="3 3" className="stroke-muted" />
                <XAxis dataKey="day" tickFormatter={(v) => v.slice(5)} className="text-xs" />
                <YAxis unit="ms" className="text-xs" />
                <Tooltip />
                <Line type="monotone" dataKey="ms" stroke="hsl(var(--primary))" strokeWidth={2} dot={false} />
              </LineChart>
            </ResponsiveContainer>
          </CardContent>
        </Card>

        <Card>
          <CardHeader><CardTitle className="text-base">Incident Summary</CardTitle></CardHeader>
          <CardContent>
            <ResponsiveContainer width="100%" height={240}>
              <BarChart data={data?.incidentTrend ?? []}>
                <CartesianGrid strokeDasharray="3 3" className="stroke-muted" />
                <XAxis dataKey="day" tickFormatter={(v) => v.slice(5)} className="text-xs" />
                <YAxis allowDecimals={false} className="text-xs" />
                <Tooltip />
                <Bar dataKey="count" fill="hsl(var(--destructive))" radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader><CardTitle className="text-base">Monitor Uptime</CardTitle></CardHeader>
        <CardContent className="p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="px-6 py-3 text-left">Monitor</th>
                <th className="px-6 py-3 text-left">Uptime</th>
                <th className="px-6 py-3 text-left">Status</th>
              </tr>
            </thead>
            <tbody>
              {(data?.monitors ?? []).map((m, i) => (
                <tr key={i} className="border-b last:border-0">
                  <td className="px-6 py-3 font-medium">{m.name}</td>
                  <td className="px-6 py-3">{m.uptime}%</td>
                  <td className="px-6 py-3">{m.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </CardContent>
      </Card>
    </div>
  );
}
