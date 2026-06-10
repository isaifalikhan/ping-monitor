import { Injectable, ForbiddenException } from '@nestjs/common';
import { UserRole } from '@netwatch/shared';
import { PrismaService } from '../../prisma/prisma.service';
import { UpdateSettingsDto } from './dto/update-settings.dto';

@Injectable()
export class SettingsService {
  constructor(private readonly prisma: PrismaService) {}

  async getSettings(organizationId: string) {
    let settings = await this.prisma.organizationSettings.findUnique({
      where: { organizationId },
    });

    if (!settings) {
      settings = await this.prisma.organizationSettings.create({
        data: { organizationId },
      });
    }

    const org = await this.prisma.organization.findUnique({
      where: { id: organizationId },
      select: { id: true, name: true, slug: true },
    });

    return {
      organization: org,
      settings: {
        latencyThresholdMs: settings.latencyThresholdMs,
        notificationEmail: settings.notificationEmail,
        timezone: settings.timezone,
      },
    };
  }

  async updateSettings(organizationId: string, role: UserRole, dto: UpdateSettingsDto) {
    if (role !== UserRole.OWNER) {
      throw new ForbiddenException('Only owners can update organization settings');
    }

    await this.prisma.organizationSettings.upsert({
      where: { organizationId },
      create: { organizationId, ...dto },
      update: dto,
    });

    return this.getSettings(organizationId);
  }
}
