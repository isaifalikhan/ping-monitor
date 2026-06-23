'use client';

import { useState } from 'react';
import { Download, FileText, BarChart3, AlertTriangle, Shield, Monitor, TrendingUp, Clock } from 'lucide-react';
import {
  BarChart,
  Bar,
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from 'recharts';
import { PageHeader } from '@/components/shared/page-header';
import { StatCard } from '@/components/dashboard/stat-card';
import { LoadingState } from '@/components/shared/loading-state';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Select } from '@/components/ui/select';
import { useReportSummary, type ReportSummaryData } from '@/hooks/use-reports';
import { reportTemplates } from '@/lib/mock-data';
import { toast } from '@/hooks/use-toast';
import { StatusBadge } from '@/components/shared/status-badge';
import { DEMO_MODE } from '@/lib/demo-mode';
import { apiClient } from '@/lib/api-client';
import { useDemoStore } from '@/stores/demo-store';
import { buildReportCsv, downloadCsv, reportCsvFilename } from '@/lib/reports/csv';

const reportIcons: Record<string, typeof FileText> = {
  'rpt-monthly': BarChart3,
  'rpt-quarterly': Shield,
  'rpt-incidents': AlertTriangle,
  'rpt-availability': TrendingUp,
};

const TEMPLATE_DAYS: Record<string, number> = {
  'rpt-monthly': 30,
  'rpt-quarterly': 90,
  'rpt-incidents': 30,
  'rpt-availability': 90,
};

function formatDateInput(date: Date) {
  return date.toISOString().slice(0, 10);
}

function presetRange(days: number) {
  const to = new Date();
  const from = new Date(to.getTime() - days * 86_400_000);
  return { from: formatDateInput(from), to: formatDateInput(to) };
}

function ChartEmpty({ message }: { message: string }) {
  return (
    <p className="flex h-[240px] items-center justify-center text-sm text-muted-foreground">{message}</p>
  );
}

async function fetchReportSummary(from?: string, to?: string): Promise<ReportSummaryData> {
  if (DEMO_MODE) {
    return useDemoStore.getState().getReportSummary(from, to);
  }
  const qs = new URLSearchParams();
  if (from) qs.set('from', from);
  if (to) qs.set('to', to);
  const query = qs.toString();
  return apiClient.get<ReportSummaryData>(`/reports/summary${query ? `?${query}` : ''}`);
}

export default function ReportsPage() {
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [exportFormat, setExportFormat] = useState('CSV');
  const [exporting, setExporting] = useState(false);
  const { data, isLoading } = useReportSummary(from || undefined, to || undefined);

  const summary = data?.summary;
  const hasResponseData = (data?.responseTrend ?? []).some((d) => d.ms > 0);
  const hasIncidentData = (data?.incidentTrend ?? []).some((d) => d.count > 0);

  const runExport = async (opts: { from?: string; to?: string; title?: string; slug?: string }) => {
    setExporting(true);
    try {
      const report = await fetchReportSummary(opts.from, opts.to);
      const csv = buildReportCsv(report, opts.title ?? 'NetWatch Report');
      downloadCsv(reportCsvFilename(report.period, opts.slug ?? 'report'), csv);
      toast({
        title: 'Export complete',
        description: `Downloaded CSV for ${report.period.from} to ${report.period.to}.`,
        variant: 'success',
      });
    } catch {
      toast({
        title: 'Export failed',
        description: 'Could not generate the report. Try again.',
        variant: 'destructive',
      });
    } finally {
      setExporting(false);
    }
  };

  const handleExport = async (format: string, reportName?: string, templateId?: string) => {
    if (format !== 'CSV') {
      toast({
        title: 'Coming soon',
        description: `${format} export is not available yet. Use CSV.`,
      });
      return;
    }

    if (templateId) {
      const days = TEMPLATE_DAYS[templateId] ?? 30;
      const range = presetRange(days);
      await runExport({ ...range, title: reportName, slug: templateId.replace('rpt-', '') });
      return;
    }

    await runExport({
      from: from || undefined,
      to: to || undefined,
      title: reportName ?? 'NetWatch Custom Report',
      slug: 'custom',
    });
  };

  const applyTemplateRange = (templateId: string) => {
    const days = TEMPLATE_DAYS[templateId] ?? 30;
    const range = presetRange(days);
    setFrom(range.from);
    setTo(range.to);
  };

  if (isLoading) return <LoadingState variant="skeleton" />;

  return (
    <div className="space-y-6">
      <PageHeader
        title="Reports"
        description={
          data?.period
            ? `Uptime, performance, and incident analytics (${data.period.from} to ${data.period.to})`
            : 'Uptime, performance, and incident analytics'
        }
      />

      <div className="grid gap-4 sm:grid-cols-2">
        {reportTemplates.map((rpt) => {
          const Icon = reportIcons[rpt.id] ?? FileText;
          const days = TEMPLATE_DAYS[rpt.id];
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
                      <Badge variant="outline">Last {days} days</Badge>
                      {summary?.avgUptime != null && from && to && (
                        <span className="text-xs text-green-500">{summary.avgUptime}% avg uptime</span>
                      )}
                    </div>
                  </div>
                  <div className="flex flex-col gap-1">
                    <Button
                      size="sm"
                      variant="ghost"
                      onClick={() => applyTemplateRange(rpt.id)}
                    >
                      View
                    </Button>
                    <Button
                      size="sm"
                      variant="outline"
                      disabled={exporting}
                      onClick={() => handleExport('CSV', rpt.title, rpt.id)}
                    >
                      <Download className="h-3 w-3 mr-1" />
                      CSV
                    </Button>
                  </div>
                </div>
              </CardContent>
            </Card>
          );
        })}
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Custom Report</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="flex flex-wrap gap-4 items-end">
            <div className="space-y-2">
              <Label>From</Label>
              <Input
                type="date"
                value={from}
                onChange={(e) => setFrom(e.target.value)}
                className="w-[160px]"
              />
            </div>
            <div className="space-y-2">
              <Label>To</Label>
              <Input type="date" value={to} onChange={(e) => setTo(e.target.value)} className="w-[160px]" />
            </div>
            <div className="space-y-2">
              <Label>Export Format</Label>
              <Select value={exportFormat} onChange={(e) => setExportFormat(e.target.value)} className="w-[120px]">
                <option value="CSV">CSV</option>
                <option value="PDF">PDF (soon)</option>
                <option value="Excel">Excel (soon)</option>
              </Select>
            </div>
            <Button className="ml-auto" disabled={exporting} onClick={() => handleExport(exportFormat)}>
              <Download className="h-4 w-4 mr-2" />
              {exporting ? 'Exporting...' : 'Generate Report'}
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
          <CardHeader>
            <CardTitle className="text-base">Response Time Trend</CardTitle>
          </CardHeader>
          <CardContent>
            {!hasResponseData ? (
              <ChartEmpty message="No response time data for the selected period." />
            ) : (
              <ResponsiveContainer width="100%" height={240}>
                <LineChart data={data?.responseTrend ?? []}>
                  <CartesianGrid strokeDasharray="3 3" className="stroke-muted" />
                  <XAxis dataKey="day" tickFormatter={(v) => v.slice(5)} className="text-xs" />
                  <YAxis unit="ms" className="text-xs" />
                  <Tooltip />
                  <Line type="monotone" dataKey="ms" stroke="hsl(var(--primary))" strokeWidth={2} dot={false} />
                </LineChart>
              </ResponsiveContainer>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="text-base">Incident Summary</CardTitle>
          </CardHeader>
          <CardContent>
            {!hasIncidentData ? (
              <ChartEmpty message="No incidents in the selected period." />
            ) : (
              <ResponsiveContainer width="100%" height={240}>
                <BarChart data={data?.incidentTrend ?? []}>
                  <CartesianGrid strokeDasharray="3 3" className="stroke-muted" />
                  <XAxis dataKey="day" tickFormatter={(v) => v.slice(5)} className="text-xs" />
                  <YAxis allowDecimals={false} className="text-xs" />
                  <Tooltip />
                  <Bar dataKey="count" fill="hsl(var(--destructive))" radius={[4, 4, 0, 0]} />
                </BarChart>
              </ResponsiveContainer>
            )}
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Monitor Uptime</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-muted-foreground">
                <th className="px-6 py-3 text-left">Monitor</th>
                <th className="px-6 py-3 text-left">Category</th>
                <th className="px-6 py-3 text-left">Uptime</th>
                <th className="px-6 py-3 text-left">Status</th>
              </tr>
            </thead>
            <tbody>
              {(data?.monitors ?? []).length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-6 py-8 text-center text-muted-foreground">
                    No monitors found.
                  </td>
                </tr>
              ) : (
                data?.monitors.map((m) => (
                  <tr key={m.name} className="border-b last:border-0 hover:bg-muted/50">
                    <td className="px-6 py-3 font-medium">{m.name}</td>
                    <td className="px-6 py-3 text-muted-foreground">{m.category ?? '—'}</td>
                    <td className="px-6 py-3">
                      <span
                        className={
                          m.uptime >= 99.9
                            ? 'text-green-500'
                            : m.uptime >= 99
                              ? 'text-amber-500'
                              : 'text-red-500'
                        }
                      >
                        {m.uptime}%
                      </span>
                    </td>
                    <td className="px-6 py-3">
                      <StatusBadge status={m.status} />
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </CardContent>
      </Card>
    </div>
  );
}
