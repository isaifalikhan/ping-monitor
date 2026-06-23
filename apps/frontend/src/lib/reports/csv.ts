import type { ReportSummaryData } from '@/lib/services/reports';

function escapeCsv(value: string | number | null | undefined) {
  const text = value == null ? '' : String(value);
  if (/[",\n\r]/.test(text)) return `"${text.replace(/"/g, '""')}"`;
  return text;
}

function row(cells: (string | number | null | undefined)[]) {
  return cells.map(escapeCsv).join(',');
}

export function buildReportCsv(data: ReportSummaryData, title = 'NetWatch Report') {
  const lines: string[] = [];
  const { summary, period } = data;

  lines.push(row([title]));
  lines.push(row(['Period', `${period.from} to ${period.to}`]));
  lines.push('');
  lines.push(row(['Summary']));
  lines.push(row(['Metric', 'Value']));
  lines.push(row(['Total Monitors', summary.totalMonitors]));
  lines.push(row(['Average Uptime (%)', summary.avgUptime]));
  lines.push(row(['Average Response Time (ms)', summary.avgResponseTime]));
  lines.push(row(['Total Incidents', summary.totalIncidents]));
  lines.push(row(['Open Incidents', summary.openIncidents]));
  lines.push(row(['Resolved Incidents', summary.resolvedIncidents]));
  lines.push('');
  lines.push(row(['Monitor Uptime']));
  lines.push(row(['Monitor', 'Category', 'Uptime (%)', 'Status']));
  for (const m of data.monitors) {
    lines.push(row([m.name, m.category, m.uptime, m.status]));
  }
  lines.push('');
  lines.push(row(['Daily Response Time']));
  lines.push(row(['Date', 'Avg Response (ms)']));
  for (const point of data.responseTrend) {
    lines.push(row([point.day, point.ms]));
  }
  lines.push('');
  lines.push(row(['Daily Incidents']));
  lines.push(row(['Date', 'Count']));
  for (const point of data.incidentTrend) {
    lines.push(row([point.day, point.count]));
  }

  return lines.join('\r\n');
}

export function downloadCsv(filename: string, content: string) {
  const blob = new Blob([content], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
}

export function reportCsvFilename(period: { from: string; to: string }, suffix = 'report') {
  return `netwatch-${suffix}-${period.from}-to-${period.to}.csv`;
}
