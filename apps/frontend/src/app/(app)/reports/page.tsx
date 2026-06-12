'use client';

import { useState } from 'react';
import { Download, FileText, BarChart3, AlertTriangle, Shield } from 'lucide-react';
import { BarChart, Bar, LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';
import { PageHeader } from '@/components/shared/page-header';
import { StatCard } from '@/components/dashboard/stat-card';
import { LoadingState } from '@/components/shared/loading-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Select } from '@/components/ui/select';
import { useReportSummary } from '@/hooks/use-reports';
import { reportTemplates } from '@/lib/mock-data';
import { toast } from '@/hooks/use-toast';
import { Monitor, TrendingUp, Clock } from 'lucide-react';
import { StatusBadge } from '@/components/shared/status-badge';

const reportIcons: Record<string, typeof FileText> = {
  'rpt-monthly': BarChart3,
  'rpt-quarterly': Shield,
  'rpt-incidents': AlertTriangle,
  'rpt-availability': TrendingUp,
};

export default function ReportsPage() {
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [exportFormat, setExportFormat] = useState('PDF');
  const { data, isLoading } = useReportSummary(from || undefined, to || undefined);

  const summary = data?.summary;

  const handleExport = (format: string, reportName?: string) => {
    toast({
      title: 'Export started',
      description: `Generating ${format} ${reportName ? `— ${reportName}` : 'report'} (demo). Download will begin shortly.`,
      variant: 'success',
    });
  };

  if (isLoading) return <LoadingState variant="skeleton" />;

  return (
    <div className="space-y-6">
      <PageHeader title="Reports" description="Uptime, performance, and incident analytics" />

      <div className="grid gap-4 sm:grid-cols-2">
        {reportTemplates.map((rpt) => {
          const Icon = reportIcons[rpt.id] ?? FileText;
          return (
            <Card key={rpt.id} className="hover:border-primary/50 transition-colors">
              <CardContent className="p-5">
                <div className="flex items-start gap-4">
                  <div className="rounded-lg bg-primary/10 p-2.5">
                    <Icon className="h-5 w-5 text-primary" />
                  </div>
                  <div className="flex-1 min-w-0">
                    <h3 className="font-semibold">{rpt.title}</h3>
                    <p className="text-sm text-muted-foreground mt-1">{rpt.description}</p>
                    <div className="flex items-center gap-2 mt-2">
                      <Badge variant="outline">{rpt.period}</Badge>
                      {rpt.avgUptime != null && (
                        <span className="text-xs text-green-500">{rpt.avgUptime}% uptime</span>
                      )}
                    </div>
                  </div>
                  <div className="flex flex-col gap-1">
                    <Button size="sm" variant="outline" onClick={() => handleExport('PDF', rpt.title)}>
                      <Download className="h-3 w-3 mr-1" />PDF
                    </Button>
                  </div>
                </div>
              </CardContent>
            </Card>
          );
        })}
      </div>

      <Card>
        <CardHeader><CardTitle className="text-base">Custom Report</CardTitle></CardHeader>
        <CardContent>
          <div className="flex flex-wrap gap-4 items-end">
            <div className="space-y-2">
              <Label>From</Label>
              <Input type="date" value={from} onChange={(e) => setFrom(e.target.value)} className="w-[160px]" />
            </div>
            <div className="space-y-2">
              <Label>To</Label>
              <Input type="date" value={to} onChange={(e) => setTo(e.target.value)} className="w-[160px]" />
            </div>
            <div className="space-y-2">
              <Label>Export Format</Label>
              <Select value={exportFormat} onChange={(e) => setExportFormat(e.target.value)} className="w-[120px]">
                <option value="PDF">PDF</option>
                <option value="CSV">CSV</option>
                <option value="Excel">Excel</option>
              </Select>
            </div>
            <Button className="ml-auto" onClick={() => handleExport(exportFormat)}>
              <Download className="h-4 w-4 mr-2" />Generate Report
            </Button>
          </div>
        </CardContent>
      </Card>

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
                <tr key={i} className="border-b last:border-0 hover:bg-muted/50">
                  <td className="px-6 py-3 font-medium">{m.name}</td>
                  <td className="px-6 py-3">
                    <span className={m.uptime >= 99.9 ? 'text-green-500' : m.uptime >= 99 ? 'text-amber-500' : 'text-red-500'}>
                      {m.uptime}%
                    </span>
                  </td>
                  <td className="px-6 py-3"><StatusBadge status={m.status} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </CardContent>
      </Card>
    </div>
  );
}
