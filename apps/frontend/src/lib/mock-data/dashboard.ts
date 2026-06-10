/**
 * MOCK DATA ONLY - replace with real API later
 * Chart trend data is static; summary stats are computed from demo store monitors/incidents.
 */
export const responseTimeTrend = [
  { time: '00:00', ms: 128 },
  { time: '04:00', ms: 115 },
  { time: '08:00', ms: 156 },
  { time: '12:00', ms: 189 },
  { time: '16:00', ms: 142 },
  { time: '20:00', ms: 131 },
  { time: 'Now', ms: 138 },
];

export const uptimeTrend = [
  { day: 'Mon', uptime: 100 },
  { day: 'Tue', uptime: 99.9 },
  { day: 'Wed', uptime: 99.5 },
  { day: 'Thu', uptime: 100 },
  { day: 'Fri', uptime: 99.8 },
  { day: 'Sat', uptime: 100 },
  { day: 'Sun', uptime: 99.9 },
];

export const incidentTrend = [
  { day: 'Mon', incidents: 0 },
  { day: 'Tue', incidents: 1 },
  { day: 'Wed', incidents: 2 },
  { day: 'Thu', incidents: 0 },
  { day: 'Fri', incidents: 1 },
  { day: 'Sat', incidents: 0 },
  { day: 'Sun', incidents: 1 },
];

export const monitorDistribution = [
  { name: 'HTTP/HTTPS', value: 3, color: 'hsl(var(--primary))' },
  { name: 'PING', value: 1, color: 'hsl(142 76% 36%)' },
  { name: 'TCP', value: 2, color: 'hsl(38 92% 50%)' },
  { name: 'DNS/SSL', value: 2, color: 'hsl(280 70% 50%)' },
];
