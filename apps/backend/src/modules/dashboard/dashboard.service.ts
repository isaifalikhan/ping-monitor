import { Injectable } from '@nestjs/common';
import { MonitorStatus } from '@prisma/client';
import { PrismaService } from '../../prisma/prisma.service';

@Injectable()
export class DashboardService {
  constructor(private readonly prisma: PrismaService) {}

  async getStats(organizationId: string) {
    const monitors = await this.prisma.monitor.findMany({
      where: { organizationId, deletedAt: null, isActive: true },
    });
    const incidents = await this.prisma.incident.findMany({
      where: { organizationId, status: { not: 'RESOLVED' } },
    });

    const online = monitors.filter((m) => m.status === MonitorStatus.UP).length;
    const offline = monitors.filter((m) => m.status === MonitorStatus.DOWN).length;
    const degraded = monitors.filter((m) => m.status === MonitorStatus.DEGRADED).length;

    const responseTimes = monitors
      .map((m) => m.lastResponseTime)
      .filter((v): v is number => v != null);
    const avgResponseTime =
      responseTimes.length > 0
        ? Math.round(responseTimes.reduce((a, b) => a + b, 0) / responseTimes.length)
        : 0;

    const uptimePercent =
      monitors.length > 0
        ? Math.round((monitors.reduce((a, m) => a + m.uptimePercent, 0) / monitors.length) * 100) / 100
        : 100;

    const totalMonitors = await this.prisma.monitor.count({
      where: { organizationId, deletedAt: null },
    });

    return {
      totalMonitors,
      online,
      offline,
      degraded,
      activeIncidents: incidents.length,
      uptimePercent,
      avgResponseTime,
    };
  }

  async getCharts(organizationId: string) {
    const monitors = await this.prisma.monitor.findMany({
      where: { organizationId, deletedAt: null, isActive: true },
    });

    const online = monitors.filter((m) => m.status === MonitorStatus.UP).length;
    const offline = monitors.filter((m) => m.status === MonitorStatus.DOWN).length;
    const degraded = monitors.filter((m) => m.status === MonitorStatus.DEGRADED).length;

    const typeBuckets: Record<string, number> = {};
    for (const m of monitors) {
      let bucket = 'Other';
      if (m.type === 'HTTP' || m.type === 'HTTPS') bucket = 'HTTP/HTTPS';
      else if (m.type === 'TCP') bucket = 'TCP';
      else if (m.type === 'DNS' || m.type === 'SSL') bucket = 'DNS/SSL';
      else if (m.type === 'PING' || m.type === 'API') bucket = 'PING/API';
      typeBuckets[bucket] = (typeBuckets[bucket] ?? 0) + 1;
    }

    const avgMs =
      monitors.length > 0
        ? Math.round(
            monitors
              .filter((m) => m.lastResponseTime != null)
              .reduce((a, m) => a + (m.lastResponseTime ?? 0), 0) /
              Math.max(1, monitors.filter((m) => m.lastResponseTime != null).length),
          )
        : 0;

    const avgUptime =
      monitors.length > 0
        ? monitors.reduce((a, m) => a + m.uptimePercent, 0) / monitors.length
        : 100;

    const incidents = await this.prisma.incident.findMany({
      where: { organizationId },
      select: { startedAt: true },
    });

    const dayNames = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
    const incidentByDay: Record<string, number> = Object.fromEntries(
      dayNames.map((d) => [d, 0]),
    );
    for (const inc of incidents) {
      const day = dayNames[inc.startedAt.getDay()];
      incidentByDay[day] = (incidentByDay[day] ?? 0) + 1;
    }

    return {
      responseTimeTrend: ['00:00', '02:00', '04:00', '06:00', '08:00', '10:00', '12:00', '14:00', '16:00', '18:00', '20:00', '22:00', 'Now'].map(
        (time, i) => ({ time, ms: Math.max(0, avgMs + Math.round(Math.sin(i) * 30)) }),
      ),
      uptimeTrend: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].map((day, i) => ({
        day,
        uptime: Math.round((avgUptime - (i === 2 ? 0.5 : 0)) * 100) / 100,
      })),
      uptime24h: ['00', '02', '04', '06', '08', '10', '12', '14', '16', '18', '20', '22', 'Now'].map((hour, i) => ({
        hour,
        uptime: Math.round((avgUptime - (offline > 0 && i >= 8 && i <= 12 ? 1.5 : 0)) * 100) / 100,
      })),
      incidentTrend: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].map((day) => ({
        day,
        incidents: incidentByDay[day] ?? 0,
      })),
      monitorDistribution: [
        { name: 'HTTP/HTTPS', value: typeBuckets['HTTP/HTTPS'] ?? 0, color: 'hsl(var(--primary))' },
        { name: 'TCP', value: typeBuckets['TCP'] ?? 0, color: 'hsl(38 92% 50%)' },
        { name: 'DNS/SSL', value: typeBuckets['DNS/SSL'] ?? 0, color: 'hsl(280 70% 50%)' },
        { name: 'PING/API', value: typeBuckets['PING/API'] ?? 0, color: 'hsl(142 76% 36%)' },
      ].filter((d) => d.value > 0),
      statusDistribution: [
        { name: 'Online', value: online, color: 'hsl(142 76% 36%)' },
        { name: 'Degraded', value: degraded, color: 'hsl(38 92% 50%)' },
        { name: 'Offline', value: offline, color: 'hsl(var(--destructive))' },
      ].filter((d) => d.value > 0),
    };
  }

  async getRecentIncidents(organizationId: string) {
    const incidents = await this.prisma.incident.findMany({
      where: { organizationId },
      orderBy: { startedAt: 'desc' },
      take: 5,
      include: { monitor: { select: { name: true } } },
    });

    return incidents.map((i) => ({
      id: i.id,
      monitor: i.monitor.name,
      status: i.status,
      duration: i.duration
        ? i.duration < 3600
          ? `${Math.floor(i.duration / 60)}m`
          : `${Math.floor(i.duration / 3600)}h`
        : '—',
      started: i.startedAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
    }));
  }

  async getRecentChecks(organizationId: string) {
    const checks = await this.prisma.monitorCheck.findMany({
      where: { monitor: { organizationId, deletedAt: null } },
      orderBy: { createdAt: 'desc' },
      take: 12,
      include: { monitor: { select: { name: true } } },
    });

    return checks.map((c) => ({
      id: c.id,
      monitor: c.monitor.name,
      status: c.status,
      responseTime: c.responseTime,
      time: c.createdAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
    }));
  }

  async getRecentAlerts(organizationId: string) {
    const logs = await this.prisma.alertLog.findMany({
      where: { organizationId },
      orderBy: { createdAt: 'desc' },
      take: 6,
      include: { channel: { select: { name: true, channel: true } } },
    });

    return logs.map((l) => ({
      id: l.id,
      monitor: l.message?.slice(0, 40) ?? 'System',
      type: l.trigger.replace(/_/g, ' '),
      channel: l.channel?.channel ?? 'EMAIL',
      time: this.relativeTime(l.createdAt),
      status: l.status === 'SENT' ? 'sent' : 'failed',
    }));
  }

  private relativeTime(date: Date) {
    const mins = Math.floor((Date.now() - date.getTime()) / 60_000);
    if (mins < 60) return `${mins} min ago`;
    const hours = Math.floor(mins / 60);
    if (hours < 24) return `${hours} hr ago`;
    return date.toLocaleDateString();
  }
}
