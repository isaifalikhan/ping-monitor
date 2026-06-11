/**
 * MOCK DATA ONLY - replace with real API later
 */
export const responseTimeTrend = [
  { time: '00:00', ms: 128 },
  { time: '02:00', ms: 115 },
  { time: '04:00', ms: 108 },
  { time: '06:00', ms: 142 },
  { time: '08:00', ms: 156 },
  { time: '10:00', ms: 189 },
  { time: '12:00', ms: 167 },
  { time: '14:00', ms: 145 },
  { time: '16:00', ms: 198 },
  { time: '18:00', ms: 142 },
  { time: '20:00', ms: 131 },
  { time: '22:00', ms: 125 },
  { time: 'Now', ms: 138 },
];

export const uptimeTrend = [
  { day: 'Mon', uptime: 100 },
  { day: 'Tue', uptime: 99.92 },
  { day: 'Wed', uptime: 99.45 },
  { day: 'Thu', uptime: 100 },
  { day: 'Fri', uptime: 99.78 },
  { day: 'Sat', uptime: 100 },
  { day: 'Sun', uptime: 99.95 },
];

export const uptime24h = [
  { hour: '00', uptime: 100 },
  { hour: '02', uptime: 100 },
  { hour: '04', uptime: 100 },
  { hour: '06', uptime: 99.9 },
  { hour: '08', uptime: 99.5 },
  { hour: '10', uptime: 98.2 },
  { hour: '12', uptime: 97.8 },
  { hour: '14', uptime: 98.5 },
  { hour: '16', uptime: 99.1 },
  { hour: '18', uptime: 99.6 },
  { hour: '20', uptime: 99.8 },
  { hour: '22', uptime: 99.9 },
  { hour: 'Now', uptime: 99.7 },
];

export const incidentTrend = [
  { day: 'Mon', incidents: 0 },
  { day: 'Tue', incidents: 1 },
  { day: 'Wed', incidents: 3 },
  { day: 'Thu', incidents: 0 },
  { day: 'Fri', incidents: 2 },
  { day: 'Sat', incidents: 0 },
  { day: 'Sun', incidents: 1 },
];

export const monitorDistribution = [
  { name: 'HTTP/HTTPS', value: 5, color: 'hsl(var(--primary))' },
  { name: 'TCP', value: 2, color: 'hsl(38 92% 50%)' },
  { name: 'DNS/SSL', value: 3, color: 'hsl(280 70% 50%)' },
  { name: 'PING/API', value: 2, color: 'hsl(142 76% 36%)' },
];

export const statusDistribution = [
  { name: 'Online', value: 8, color: 'hsl(142 76% 36%)' },
  { name: 'Degraded', value: 3, color: 'hsl(38 92% 50%)' },
  { name: 'Offline', value: 1, color: 'hsl(var(--destructive))' },
];
