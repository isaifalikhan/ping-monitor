import { Injectable, NotFoundException, ForbiddenException } from '@nestjs/common';
import { Prisma, MonitorStatus } from '@prisma/client';
import { UserRole } from '@netwatch/shared';
import { PrismaService } from '../../prisma/prisma.service';
import { CreateMonitorDto } from './dto/create-monitor.dto';
import { UpdateMonitorDto } from './dto/update-monitor.dto';
import { QueryMonitorsDto } from './dto/query-monitors.dto';

@Injectable()
export class MonitorsService {
  constructor(private readonly prisma: PrismaService) {}

  private mapMonitor(m: {
    id: string;
    name: string;
    description: string | null;
    type: string;
    target: string;
    status: string;
    tags: string[];
    group: string | null;
    checkInterval: number;
    timeout: number;
    retryCount: number;
    isActive: boolean;
    maintenanceMode: boolean;
    lastResponseTime: number | null;
    lastCheckedAt: Date | null;
    uptimePercent: number;
    createdAt: Date;
    updatedAt: Date;
  }) {
    return {
      id: m.id,
      name: m.name,
      description: m.description,
      type: m.type,
      target: m.target,
      status: m.status,
      tags: m.tags,
      group: m.group,
      checkInterval: m.checkInterval,
      timeout: m.timeout,
      retryCount: m.retryCount,
      isActive: m.isActive,
      maintenanceMode: m.maintenanceMode,
      lastResponseTime: m.lastResponseTime,
      lastCheckedAt: m.lastCheckedAt?.toISOString() ?? null,
      uptimePercent: m.uptimePercent,
      createdAt: m.createdAt.toISOString(),
      updatedAt: m.updatedAt.toISOString(),
    };
  }

  async findAll(organizationId: string, query: QueryMonitorsDto) {
    const page = query.page ?? 1;
    const limit = query.limit ?? 20;
    const where: Prisma.MonitorWhereInput = {
      organizationId,
      deletedAt: null,
      ...(query.status && { status: query.status }),
      ...(query.type && { type: query.type }),
      ...(query.search && {
        OR: [
          { name: { contains: query.search, mode: 'insensitive' } },
          { target: { contains: query.search, mode: 'insensitive' } },
        ],
      }),
    };

    const [items, total] = await Promise.all([
      this.prisma.monitor.findMany({
        where,
        orderBy: { createdAt: 'desc' },
        skip: (page - 1) * limit,
        take: limit,
      }),
      this.prisma.monitor.count({ where }),
    ]);

    return {
      items: items.map((m) => this.mapMonitor(m)),
      total,
      page,
      limit,
      totalPages: Math.ceil(total / limit),
    };
  }

  async findOne(organizationId: string, id: string) {
    const monitor = await this.prisma.monitor.findFirst({
      where: { id, organizationId, deletedAt: null },
    });
    if (!monitor) throw new NotFoundException('Monitor not found');
    return this.mapMonitor(monitor);
  }

  async create(organizationId: string, dto: CreateMonitorDto) {
    const monitor = await this.prisma.monitor.create({
      data: {
        name: dto.name,
        description: dto.description,
        type: dto.type,
        target: dto.target,
        checkInterval: dto.checkInterval ?? 60,
        timeout: dto.timeout ?? 30,
        retryCount: dto.retryCount ?? 3,
        group: dto.group,
        tags: dto.tags ?? [],
        organizationId,
        status: MonitorStatus.UNKNOWN,
      },
    });
    return this.mapMonitor(monitor);
  }

  async update(organizationId: string, id: string, dto: UpdateMonitorDto, role: UserRole) {
    if (role === UserRole.VIEWER) {
      throw new ForbiddenException('Viewers cannot modify monitors');
    }
    await this.findOne(organizationId, id);
    const monitor = await this.prisma.monitor.update({
      where: { id },
      data: dto,
    });
    return this.mapMonitor(monitor);
  }

  async remove(organizationId: string, id: string, role: UserRole) {
    if (role === UserRole.VIEWER) {
      throw new ForbiddenException('Viewers cannot delete monitors');
    }
    await this.findOne(organizationId, id);
    await this.prisma.monitor.update({
      where: { id },
      data: { deletedAt: new Date(), isActive: false },
    });
    return { message: 'Monitor deleted' };
  }

  async getChecks(organizationId: string, id: string, limit = 50) {
    await this.findOne(organizationId, id);
    const checks = await this.prisma.monitorCheck.findMany({
      where: { monitorId: id },
      orderBy: { createdAt: 'desc' },
      take: limit,
    });
    return checks.map((c) => ({
      id: c.id,
      status: c.status,
      responseTime: c.responseTime,
      packetLoss: c.packetLoss,
      errorMessage: c.errorMessage,
      createdAt: c.createdAt.toISOString(),
    }));
  }

  async getDetail(organizationId: string, id: string) {
    const monitor = await this.findOne(organizationId, id);
    const [checks, incidents, alertLogs] = await Promise.all([
      this.getChecks(organizationId, id, 20),
      this.prisma.incident.findMany({
        where: { monitorId: id, organizationId },
        orderBy: { startedAt: 'desc' },
        take: 10,
        include: {
          assignedUser: { select: { id: true, firstName: true, lastName: true } },
        },
      }),
      this.prisma.alertLog.findMany({
        where: { monitorId: id, organizationId },
        orderBy: { createdAt: 'desc' },
        take: 10,
      }),
    ]);

    return {
      monitor,
      checks,
      incidents: incidents.map((i) => ({
        id: i.id,
        status: i.status,
        severity: i.severity,
        startedAt: i.startedAt.toISOString(),
        endedAt: i.endedAt?.toISOString() ?? null,
        duration: i.duration,
        assignedUser: i.assignedUser,
      })),
      alertHistory: alertLogs.map((a) => ({
        id: a.id,
        trigger: a.trigger,
        status: a.status,
        message: a.message,
        createdAt: a.createdAt.toISOString(),
      })),
    };
  }
}
