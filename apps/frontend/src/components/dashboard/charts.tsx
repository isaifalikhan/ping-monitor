'use client';

import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Line,
  LineChart,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useDashboardCharts } from '@/hooks/use-dashboard';

function ChartEmpty({ message }: { message: string }) {
  return (
    <p className="flex h-[240px] items-center justify-center text-sm text-muted-foreground">
      {message}
    </p>
  );
}

function uptimeDomain(data: { uptime: number }[]) {
  if (!data.length) return [95, 100] as [number, number];
  const values = data.map((d) => d.uptime);
  const min = Math.min(...values);
  return [Math.max(0, Math.floor(min - 1)), 100] as [number, number];
}

export function ResponseTimeChart() {
  const { responseTimeTrend, isLoading } = useDashboardCharts();
  const hasData = responseTimeTrend.some((d) => d.ms > 0);

  return (
    <Card className="col-span-2">
      <CardHeader>
        <CardTitle className="text-base">Response Time (24h)</CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <ChartEmpty message="Loading check history..." />
        ) : !hasData ? (
          <ChartEmpty message="No response time data in the last 24 hours." />
        ) : (
          <ResponsiveContainer width="100%" height={240}>
            <AreaChart data={responseTimeTrend}>
              <defs>
                <linearGradient id="rtGradient" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="5%" stopColor="hsl(var(--primary))" stopOpacity={0.3} />
                  <stop offset="95%" stopColor="hsl(var(--primary))" stopOpacity={0} />
                </linearGradient>
              </defs>
              <CartesianGrid strokeDasharray="3 3" className="stroke-muted" />
              <XAxis dataKey="time" className="text-xs" />
              <YAxis unit="ms" className="text-xs" />
              <Tooltip />
              <Area
                type="monotone"
                dataKey="ms"
                stroke="hsl(var(--primary))"
                fill="url(#rtGradient)"
              />
            </AreaChart>
          </ResponsiveContainer>
        )}
      </CardContent>
    </Card>
  );
}

export function UptimeChart() {
  const { uptimeTrend, isLoading } = useDashboardCharts();

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Uptime Trend (7d)</CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <ChartEmpty message="Loading uptime trend..." />
        ) : (
          <ResponsiveContainer width="100%" height={240}>
            <LineChart data={uptimeTrend}>
              <CartesianGrid strokeDasharray="3 3" className="stroke-muted" />
              <XAxis dataKey="day" className="text-xs" />
              <YAxis domain={uptimeDomain(uptimeTrend)} unit="%" className="text-xs" />
              <Tooltip />
              <Line
                type="monotone"
                dataKey="uptime"
                stroke="hsl(142 76% 36%)"
                strokeWidth={2}
                dot={false}
              />
            </LineChart>
          </ResponsiveContainer>
        )}
      </CardContent>
    </Card>
  );
}

export function IncidentChart() {
  const { incidentTrend, isLoading } = useDashboardCharts();

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Incidents (7d)</CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <ChartEmpty message="Loading incidents..." />
        ) : (
          <ResponsiveContainer width="100%" height={240}>
            <BarChart data={incidentTrend}>
              <CartesianGrid strokeDasharray="3 3" className="stroke-muted" />
              <XAxis dataKey="day" className="text-xs" />
              <YAxis allowDecimals={false} className="text-xs" />
              <Tooltip />
              <Bar dataKey="incidents" fill="hsl(var(--destructive))" radius={[4, 4, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        )}
      </CardContent>
    </Card>
  );
}

export function Uptime24hChart() {
  const { uptime24h, isLoading } = useDashboardCharts();

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Uptime (24h)</CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <ChartEmpty message="Loading uptime..." />
        ) : (
          <ResponsiveContainer width="100%" height={240}>
            <AreaChart data={uptime24h}>
              <defs>
                <linearGradient id="uptimeGradient" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="5%" stopColor="hsl(142 76% 36%)" stopOpacity={0.3} />
                  <stop offset="95%" stopColor="hsl(142 76% 36%)" stopOpacity={0} />
                </linearGradient>
              </defs>
              <CartesianGrid strokeDasharray="3 3" className="stroke-muted" />
              <XAxis dataKey="hour" className="text-xs" />
              <YAxis domain={uptimeDomain(uptime24h)} unit="%" className="text-xs" />
              <Tooltip />
              <Area
                type="monotone"
                dataKey="uptime"
                stroke="hsl(142 76% 36%)"
                fill="url(#uptimeGradient)"
              />
            </AreaChart>
          </ResponsiveContainer>
        )}
      </CardContent>
    </Card>
  );
}

export function StatusDistributionChart() {
  const { statusDistribution, isLoading } = useDashboardCharts();

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Status Distribution</CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <ChartEmpty message="Loading status..." />
        ) : statusDistribution.length === 0 ? (
          <ChartEmpty message="No active monitors." />
        ) : (
          <>
            <ResponsiveContainer width="100%" height={240}>
              <PieChart>
                <Pie
                  data={statusDistribution}
                  dataKey="value"
                  nameKey="name"
                  cx="50%"
                  cy="50%"
                  innerRadius={50}
                  outerRadius={80}
                  paddingAngle={2}
                >
                  {statusDistribution.map((entry) => (
                    <Cell key={entry.name} fill={entry.color} />
                  ))}
                </Pie>
                <Tooltip />
              </PieChart>
            </ResponsiveContainer>
            <div className="mt-2 grid grid-cols-2 gap-2 text-xs">
              {statusDistribution.map((item) => (
                <div key={item.name} className="flex items-center gap-2">
                  <div className="h-2 w-2 rounded-full" style={{ background: item.color }} />
                  <span className="text-muted-foreground">{item.name}</span>
                  <span className="ml-auto font-medium">{item.value}</span>
                </div>
              ))}
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}

export function MonitorDistributionChart() {
  const { categoryDistribution, isLoading } = useDashboardCharts();

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Category Distribution</CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <ChartEmpty message="Loading categories..." />
        ) : categoryDistribution.length === 0 ? (
          <ChartEmpty message="No monitors with categories yet." />
        ) : (
          <>
            <ResponsiveContainer width="100%" height={240}>
              <PieChart>
                <Pie
                  data={categoryDistribution}
                  dataKey="value"
                  nameKey="name"
                  cx="50%"
                  cy="50%"
                  innerRadius={50}
                  outerRadius={80}
                  paddingAngle={2}
                >
                  {categoryDistribution.map((entry) => (
                    <Cell key={entry.name} fill={entry.color} />
                  ))}
                </Pie>
                <Tooltip />
              </PieChart>
            </ResponsiveContainer>
            <div className="mt-2 grid grid-cols-2 gap-2 text-xs">
              {categoryDistribution.map((item) => (
                <div key={item.name} className="flex items-center gap-2">
                  <div className="h-2 w-2 rounded-full" style={{ background: item.color }} />
                  <span className="text-muted-foreground">{item.name}</span>
                  <span className="ml-auto font-medium">{item.value}</span>
                </div>
              ))}
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}
