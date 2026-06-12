import { Injectable, NotFoundException } from '@nestjs/common';
import { IncidentStatus, Prisma } from '@prisma/client';
import { PrismaService } from '../../prisma/prisma.service';
import { AuditService } from '../audit/audit.service';

@Injectable()
export class IncidentsService {
  constructor(
    private readonly prisma: PrismaService,
    private readonly auditService: AuditService,
  ) {}

  async findAll(organizationId: string, status?: IncidentStatus) {
    const where: Prisma.IncidentWhereInput = {
      organizationId,
      ...(status && { status }),
    };

    const incidents = await this.prisma.incident.findMany({
      where,
      orderBy: { startedAt: 'desc' },
      include: {
        monitor: { select: { id: true, name: true, target: true } },
        assignedUser: { select: { id: true, firstName: true, lastName: true, email: true } },
      },
    });

    return incidents.map((i) => ({
      id: i.id,
      monitor: i.monitor,
      status: i.status,
      severity: i.severity,
      startedAt: i.startedAt.toISOString(),
      endedAt: i.endedAt?.toISOString() ?? null,
      duration: i.duration,
      rootCause: i.rootCause,
      notes: i.notes,
      assignedUser: i.assignedUser,
    }));
  }

  async acknowledge(organizationId: string, id: string, userId: string, actor?: string) {
    const result = await this.updateStatus(organizationId, id, IncidentStatus.ACKNOWLEDGED, userId);
    void this.auditService.log({
      organizationId,
      category: 'INCIDENT',
      action: `Acknowledged incident ${id}`,
      actor: actor ?? 'System',
      target: result.monitor.name,
      actorUserId: userId,
    });
    return result;
  }

  async resolve(organizationId: string, id: string, actor?: string) {
    const incident = await this.getIncident(organizationId, id);
    const endedAt = new Date();
    const duration = Math.floor((endedAt.getTime() - incident.startedAt.getTime()) / 1000);

    const updated = await this.prisma.incident.update({
      where: { id },
      data: { status: IncidentStatus.RESOLVED, endedAt, duration },
      include: {
        monitor: { select: { id: true, name: true } },
        assignedUser: { select: { id: true, firstName: true, lastName: true } },
      },
    });

    void this.auditService.log({
      organizationId,
      category: 'INCIDENT',
      action: `Resolved incident ${id}`,
      actor: actor ?? 'System',
      target: updated.monitor.name,
    });
    return this.mapIncident(updated);
  }

  private async updateStatus(
    organizationId: string,
    id: string,
    status: IncidentStatus,
    userId?: string,
  ) {
    await this.getIncident(organizationId, id);
    const updated = await this.prisma.incident.update({
      where: { id },
      data: {
        status,
        ...(userId && { assignedUserId: userId }),
      },
      include: {
        monitor: { select: { id: true, name: true } },
        assignedUser: { select: { id: true, firstName: true, lastName: true } },
      },
    });
    return this.mapIncident(updated);
  }

  private async getIncident(organizationId: string, id: string) {
    const incident = await this.prisma.incident.findFirst({
      where: { id, organizationId },
    });
    if (!incident) throw new NotFoundException('Incident not found');
    return incident;
  }

  private mapIncident(i: {
    id: string;
    status: string;
    severity: string;
    startedAt: Date;
    endedAt: Date | null;
    duration: number | null;
    rootCause: string | null;
    notes: string | null;
    monitor: { id: string; name: string };
    assignedUser: { id: string; firstName: string; lastName: string } | null;
  }) {
    return {
      id: i.id,
      monitor: i.monitor,
      status: i.status,
      severity: i.severity,
      startedAt: i.startedAt.toISOString(),
      endedAt: i.endedAt?.toISOString() ?? null,
      duration: i.duration,
      rootCause: i.rootCause,
      notes: i.notes,
      assignedUser: i.assignedUser,
    };
  }
}
