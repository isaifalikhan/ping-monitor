/**
 * MOCK DATA ONLY - replace with real API later
 */
export const reportTemplates = [
  {
    id: 'rpt-monthly',
    title: 'Monthly Uptime Report',
    description: 'Comprehensive uptime and availability metrics for the past 30 days',
    period: 'June 2026',
    monitors: 12,
    avgUptime: 99.42,
  },
  {
    id: 'rpt-quarterly',
    title: 'Quarterly SLA Report',
    description: 'SLA compliance analysis against 99.9% availability target',
    period: 'Q2 2026',
    monitors: 12,
    avgUptime: 99.67,
  },
  {
    id: 'rpt-incidents',
    title: 'Incident Report',
    description: 'Incident summary, MTTR, and root cause analysis',
    period: 'Last 30 days',
    monitors: 6,
    avgUptime: null,
  },
  {
    id: 'rpt-availability',
    title: 'Availability Report',
    description: 'Per-monitor availability breakdown with downtime analysis',
    period: 'Last 90 days',
    monitors: 12,
    avgUptime: 99.51,
  },
];
