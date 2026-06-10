import { Injectable } from '@nestjs/common';
import { PrismaService } from '../../prisma/prisma.service';
import { CreateMaintenanceDto } from './dto/create-maintenance.dto';

@Injectable()
export class MaintenanceService {
  constructor(private readonly prisma: PrismaService) {}

  async findAll(organizationId: string) {
    const windows = await this.prisma.maintenanceWindow.findMany({
      where: { organizationId },
      orderBy: { startTime: 'desc' },
      include: { monitor: { select: { id: true, name: true } } },
    });

    return windows.map((w) => ({
      id: w.id,
      title: w.title,
      monitor: w.monitor,
      startTime: w.startTime.toISOString(),
      endTime: w.endTime.toISOString(),
      reason: w.reason,
      pauseAlerts: w.pauseAlerts,
      createdAt: w.createdAt.toISOString(),
    }));
  }

  async create(organizationId: string, dto: CreateMaintenanceDto) {
    const window = await this.prisma.maintenanceWindow.create({
      data: {
        organizationId,
        title: dto.title,
        monitorId: dto.monitorId,
        startTime: new Date(dto.startTime),
        endTime: new Date(dto.endTime),
        reason: dto.reason,
        pauseAlerts: dto.pauseAlerts ?? true,
      },
      include: { monitor: { select: { id: true, name: true } } },
    });

    return {
      id: window.id,
      title: window.title,
      monitor: window.monitor,
      startTime: window.startTime.toISOString(),
      endTime: window.endTime.toISOString(),
      reason: window.reason,
      pauseAlerts: window.pauseAlerts,
    };
  }
}
