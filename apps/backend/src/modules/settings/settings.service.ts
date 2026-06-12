import { Injectable, ForbiddenException } from '@nestjs/common';
import { UserRole } from '@netwatch/shared';
import { Prisma } from '@prisma/client';
import { PrismaService } from '../../prisma/prisma.service';
import { AuditService } from '../audit/audit.service';
import { UpdateSettingsDto } from './dto/update-settings.dto';

const DEFAULT_PREFERENCES = {
  defaultCheckInterval: 60,
  defaultTimeout: 30,
  retentionDays: 90,
  statusPageSlug: '',
  sessionTimeoutMinutes: 480,
  requireMfa: false,
  ipAllowlist: '',
  brandPrimaryColor: '#3b82f6',
  brandLogoUrl: '',
  slackWebhook: '',
  pagerdutyKey: '',
  datadogApiKey: '',
};

@Injectable()
export class SettingsService {
  constructor(
    private readonly prisma: PrismaService,
    private readonly auditService: AuditService,
  ) {}

  private mapSettings(settings: {
    latencyThresholdMs: number;
    notificationEmail: string | null;
    timezone: string;
    enablePublicStatusPage: boolean;
    preferences: unknown;
  }) {
    const prefs = {
      ...DEFAULT_PREFERENCES,
      ...(typeof settings.preferences === 'object' && settings.preferences !== null
        ? (settings.preferences as Record<string, unknown>)
        : {}),
    };

    return {
      latencyThresholdMs: settings.latencyThresholdMs,
      notificationEmail: settings.notificationEmail ?? '',
      timezone: settings.timezone,
      enablePublicStatusPage: settings.enablePublicStatusPage,
      statusPageSlug: String(prefs.statusPageSlug ?? ''),
      defaultCheckInterval: Number(prefs.defaultCheckInterval ?? 60),
      defaultTimeout: Number(prefs.defaultTimeout ?? 30),
      retentionDays: Number(prefs.retentionDays ?? 90),
      sessionTimeoutMinutes: Number(prefs.sessionTimeoutMinutes ?? 480),
      requireMfa: Boolean(prefs.requireMfa ?? false),
      ipAllowlist: String(prefs.ipAllowlist ?? ''),
      brandPrimaryColor: String(prefs.brandPrimaryColor ?? '#3b82f6'),
      brandLogoUrl: String(prefs.brandLogoUrl ?? ''),
      slackWebhook: String(prefs.slackWebhook ?? ''),
      pagerdutyKey: String(prefs.pagerdutyKey ?? ''),
      datadogApiKey: String(prefs.datadogApiKey ?? ''),
    };
  }

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
        ...this.mapSettings(settings),
        statusPageSlug: org?.slug ?? '',
      },
    };
  }

  async updateSettings(
    organizationId: string,
    role: UserRole,
    dto: UpdateSettingsDto,
    actor?: string,
  ) {
    if (role !== UserRole.OWNER) {
      throw new ForbiddenException('Only owners can update organization settings');
    }

    const existing = await this.prisma.organizationSettings.findUnique({
      where: { organizationId },
    });

    const currentPrefs =
      typeof existing?.preferences === 'object' && existing?.preferences !== null
        ? (existing.preferences as Record<string, unknown>)
        : {};

    const { preferences: dtoPrefs, ...scalarFields } = dto;
    const mergedPrefs = dtoPrefs ? { ...currentPrefs, ...dtoPrefs } : currentPrefs;

    await this.prisma.organizationSettings.upsert({
      where: { organizationId },
      create: {
        organizationId,
        ...scalarFields,
        preferences: mergedPrefs as Prisma.InputJsonValue,
      },
      update: {
        ...scalarFields,
        ...(dtoPrefs && { preferences: mergedPrefs as Prisma.InputJsonValue }),
      },
    });

    void this.auditService.log({
      organizationId,
      category: 'SETTINGS',
      action: 'Updated organization settings',
      actor: actor ?? 'System',
      target: 'Organization settings',
    });

    return this.getSettings(organizationId);
  }
}
