import { Injectable } from '@nestjs/common';
import { AuditLogCategory, Prisma } from '@prisma/client';
import { PrismaService } from '../../prisma/prisma.service';

export interface AuditLogInput {
  organizationId: string;
  category: AuditLogCategory;
  action: string;
  actor: string;
  actorUserId?: string;
  target?: string;
  ipAddress?: string;
}

@Injectable()
export class AuditService {
  constructor(private readonly prisma: PrismaService) {}

  async log(input: AuditLogInput) {
    return this.prisma.auditLog.create({ data: input });
  }

  async findAll(organizationId: string, category?: AuditLogCategory) {
    const where: Prisma.AuditLogWhereInput = {
      organizationId,
      ...(category && { category }),
    };

    const logs = await this.prisma.auditLog.findMany({
      where,
      orderBy: { createdAt: 'desc' },
      take: 100,
    });

    return logs.map((l) => ({
      id: l.id,
      category: l.category,
      action: l.action,
      actor: l.actor,
      target: l.target,
      ipAddress: l.ipAddress,
      createdAt: l.createdAt.toISOString(),
    }));
  }
}
