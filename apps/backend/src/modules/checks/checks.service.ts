import { Injectable, Logger } from '@nestjs/common';
import { IncidentSeverity, IncidentStatus, MonitorStatus } from '@prisma/client';
import { PrismaService } from '../../prisma/prisma.service';
import { CheckExecutorService } from './check-executor.service';
import { AuditService } from '../audit/audit.service';

@Injectable()
export class ChecksService {
  private readonly logger = new Logger(ChecksService.name);

  constructor(
    private readonly prisma: PrismaService,
    private readonly executor: CheckExecutorService,
    private readonly auditService: AuditService,
  ) {}

  async runDueChecks() {
    const monitors = await this.prisma.monitor.findMany({
      where: {
        isActive: true,
        deletedAt: null,
        maintenanceMode: false,
      },
    });

    const now = Date.now();
    const due = monitors.filter((m) => {
      if (!m.lastCheckedAt) return true;
      return now - m.lastCheckedAt.getTime() >= m.checkInterval * 1000;
    });

    for (const monitor of due) {
      try {
        await this.runCheck(monitor.id);
      } catch (err) {
        this.logger.error(`Failed check for monitor ${monitor.id}`, err);
      }
    }

    return { processed: due.length };
  }

  async runCheck(monitorId: string) {
    const monitor = await this.prisma.monitor.findFirst({
      where: { id: monitorId, deletedAt: null },
    });
    if (!monitor || !monitor.isActive || monitor.maintenanceMode) return null;

    const settings = await this.prisma.organizationSettings.findUnique({
      where: { organizationId: monitor.organizationId },
    });
    const latencyThreshold = settings?.latencyThresholdMs ?? 500;

    const result = await this.executor.execute(
      monitor.type,
      monitor.target,
      monitor.timeout,
      latencyThreshold,
    );

    const prismaStatus = result.status as MonitorStatus;

    await this.prisma.monitorCheck.create({
      data: {
        monitorId: monitor.id,
        status: prismaStatus,
        responseTime: result.responseTime,
        errorMessage: result.errorMessage,
      },
    });

    const previousStatus = monitor.status;
    const uptimePercent = await this.calculateUptime(monitor.id);

    await this.prisma.monitor.update({
      where: { id: monitor.id },
      data: {
        status: prismaStatus,
        lastResponseTime: result.responseTime,
        lastCheckedAt: new Date(),
        uptimePercent,
      },
    });

    await this.handleIncidentTransition(
      monitor.id,
      monitor.organizationId,
      previousStatus,
      prismaStatus,
      result.errorMessage,
    );

    return result;
  }

  private async calculateUptime(monitorId: string): Promise<number> {
    const checks = await this.prisma.monitorCheck.findMany({
      where: { monitorId },
      orderBy: { createdAt: 'desc' },
      take: 100,
    });
    if (!checks.length) return 100;
    const up = checks.filter((c) => c.status === MonitorStatus.UP).length;
    return Math.round((up / checks.length) * 10000) / 100;
  }

  private async handleIncidentTransition(
    monitorId: string,
    organizationId: string,
    previous: MonitorStatus,
    current: MonitorStatus,
    errorMessage?: string,
  ) {
    const isFailure = current === MonitorStatus.DOWN || current === MonitorStatus.DEGRADED;
    const wasHealthy = previous === MonitorStatus.UP || previous === MonitorStatus.UNKNOWN;

    if (isFailure && wasHealthy) {
      const open = await this.prisma.incident.findFirst({
        where: { monitorId, organizationId, status: { not: IncidentStatus.RESOLVED } },
      });
      if (!open) {
        const incident = await this.prisma.incident.create({
          data: {
            monitorId,
            organizationId,
            status: IncidentStatus.OPEN,
            severity:
              current === MonitorStatus.DOWN ? IncidentSeverity.CRITICAL : IncidentSeverity.MEDIUM,
            startedAt: new Date(),
            rootCause: errorMessage ?? null,
          },
          include: { monitor: { select: { name: true } } },
        });
        void this.auditService.log({
          organizationId,
          category: 'INCIDENT',
          action: `Auto-opened incident for ${incident.monitor.name}`,
          actor: 'System',
          target: incident.id,
        });
      }
      return;
    }

    if (current === MonitorStatus.UP && previous !== MonitorStatus.UP) {
      const openIncidents = await this.prisma.incident.findMany({
        where: { monitorId, organizationId, status: { not: IncidentStatus.RESOLVED } },
      });
      const endedAt = new Date();
      for (const inc of openIncidents) {
        const duration = Math.floor((endedAt.getTime() - inc.startedAt.getTime()) / 1000);
        await this.prisma.incident.update({
          where: { id: inc.id },
          data: { status: IncidentStatus.RESOLVED, endedAt, duration },
        });
      }
    }
  }
}
