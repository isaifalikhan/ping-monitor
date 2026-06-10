import { Injectable } from '@nestjs/common';
import { PrismaService } from '../../prisma/prisma.service';

@Injectable()
export class ReportsService {
  constructor(private readonly prisma: PrismaService) {}

  async getSummary(organizationId: string, from?: string, to?: string) {
    const fromDate = from ? new Date(from) : new Date(Date.now() - 30 * 24 * 60 * 60 * 1000);
    const toDate = to ? new Date(to) : new Date();

    const [monitors, incidents, checks] = await Promise.all([
      this.prisma.monitor.findMany({
        where: { organizationId, deletedAt: null },
        select: { id: true, name: true, uptimePercent: true, status: true },
      }),
      this.prisma.incident.findMany({
        where: {
          organizationId,
          startedAt: { gte: fromDate, lte: toDate },
        },
      }),
      this.prisma.monitorCheck.findMany({
        where: {
          createdAt: { gte: fromDate, lte: toDate },
          monitor: { organizationId },
        },
        select: { responseTime: true, status: true, createdAt: true },
      }),
    ]);

    const avgUptime =
      monitors.length > 0
        ? monitors.reduce((sum, m) => sum + m.uptimePercent, 0) / monitors.length
        : 100;

    const responseTimes = checks
      .filter((c) => c.responseTime != null)
      .map((c) => c.responseTime as number);
    const avgResponse =
      responseTimes.length > 0
        ? Math.round(responseTimes.reduce((a, b) => a + b, 0) / responseTimes.length)
        : 0;

    const incidentsByDay = this.groupByDay(
      incidents.map((i) => i.startedAt),
      fromDate,
      toDate,
    );

    const responseByDay = this.avgResponseByDay(checks, fromDate, toDate);

    return {
      period: { from: fromDate.toISOString(), to: toDate.toISOString() },
      summary: {
        totalMonitors: monitors.length,
        avgUptime: Math.round(avgUptime * 100) / 100,
        avgResponseTime: avgResponse,
        totalIncidents: incidents.length,
        openIncidents: incidents.filter((i) => i.status === 'OPEN').length,
      },
      monitors: monitors.map((m) => ({
        name: m.name,
        uptime: m.uptimePercent,
        status: m.status,
      })),
      incidentTrend: incidentsByDay,
      responseTrend: responseByDay,
    };
  }

  private groupByDay(dates: Date[], from: Date, to: Date) {
    const days: { day: string; count: number }[] = [];
    const cursor = new Date(from);
    while (cursor <= to) {
      const key = cursor.toISOString().slice(0, 10);
      const count = dates.filter((d) => d.toISOString().slice(0, 10) === key).length;
      days.push({ day: key, count });
      cursor.setDate(cursor.getDate() + 1);
    }
    return days.slice(-30);
  }

  private avgResponseByDay(
    checks: { responseTime: number | null; createdAt: Date }[],
    from: Date,
    to: Date,
  ) {
    const days: { day: string; ms: number }[] = [];
    const cursor = new Date(from);
    while (cursor <= to) {
      const key = cursor.toISOString().slice(0, 10);
      const dayChecks = checks.filter(
        (c) => c.createdAt.toISOString().slice(0, 10) === key && c.responseTime != null,
      );
      const avg =
        dayChecks.length > 0
          ? Math.round(
              dayChecks.reduce((s, c) => s + (c.responseTime ?? 0), 0) / dayChecks.length,
            )
          : 0;
      days.push({ day: key, ms: avg });
      cursor.setDate(cursor.getDate() + 1);
    }
    return days.slice(-30);
  }
}
