import { Injectable, NotFoundException } from '@nestjs/common';
import { Prisma } from '@prisma/client';
import { PrismaService } from '../../prisma/prisma.service';
import { MailService } from '../mail/mail.service';
import { CreateAlertChannelDto } from './dto/create-channel.dto';
import { CreateAlertRuleDto } from './dto/create-rule.dto';

@Injectable()
export class AlertsService {
  constructor(
    private readonly prisma: PrismaService,
    private readonly mailService: MailService,
  ) {}

  async getChannels(organizationId: string) {
    const channels = await this.prisma.alertChannelConfig.findMany({
      where: { organizationId },
      orderBy: { createdAt: 'desc' },
      include: { _count: { select: { rules: true } } },
    });
    return channels.map((c) => ({
      id: c.id,
      channel: c.channel,
      name: c.name,
      config: c.config,
      isActive: c.isActive,
      rulesCount: c._count.rules,
      createdAt: c.createdAt.toISOString(),
    }));
  }

  async createChannel(organizationId: string, dto: CreateAlertChannelDto) {
    const channel = await this.prisma.alertChannelConfig.create({
      data: {
        organizationId,
        channel: dto.channel,
        name: dto.name,
        config: (dto.config ?? {}) as Prisma.InputJsonValue,
      },
    });
    return {
      id: channel.id,
      channel: channel.channel,
      name: channel.name,
      config: channel.config,
      isActive: channel.isActive,
    };
  }

  async getRules(organizationId: string) {
    const rules = await this.prisma.alertRule.findMany({
      where: { organizationId },
      include: {
        channel: { select: { id: true, name: true, channel: true } },
        monitor: { select: { id: true, name: true } },
      },
      orderBy: { createdAt: 'desc' },
    });
    return rules.map((r) => ({
      id: r.id,
      trigger: r.trigger,
      isActive: r.isActive,
      channel: r.channel,
      monitor: r.monitor,
    }));
  }

  async createRule(organizationId: string, dto: CreateAlertRuleDto) {
    const channel = await this.prisma.alertChannelConfig.findFirst({
      where: { id: dto.channelId, organizationId },
    });
    if (!channel) throw new NotFoundException('Alert channel not found');

    const rule = await this.prisma.alertRule.create({
      data: {
        organizationId,
        channelId: dto.channelId,
        trigger: dto.trigger,
        monitorId: dto.monitorId,
      },
      include: {
        channel: { select: { id: true, name: true, channel: true } },
        monitor: { select: { id: true, name: true } },
      },
    });

    return {
      id: rule.id,
      trigger: rule.trigger,
      isActive: rule.isActive,
      channel: rule.channel,
      monitor: rule.monitor,
    };
  }

  async testAlert(organizationId: string, channelId: string) {
    const channel = await this.prisma.alertChannelConfig.findFirst({
      where: { id: channelId, organizationId },
    });
    if (!channel) throw new NotFoundException('Alert channel not found');

    const config = channel.config as Record<string, string>;
    let status: 'SENT' | 'FAILED' = 'SENT';
    let message = 'Test alert sent successfully';

    try {
      if (channel.channel === 'EMAIL') {
        const email = config.email || config.address;
        if (!email) throw new Error('Email address not configured');
        await this.mailService.sendMail({
          to: email,
          subject: 'NetWatch Test Alert',
          html: '<p>This is a test alert from NetWatch.</p>',
        });
      } else {
        message = `Test alert simulated for ${channel.channel} channel`;
      }
    } catch (error) {
      status = 'FAILED';
      message = (error as Error).message;
    }

    await this.prisma.alertLog.create({
      data: {
        organizationId,
        channelId,
        trigger: 'MONITOR_DOWN',
        status,
        message,
      },
    });

    return { status, message };
  }

  async getLogs(organizationId: string, limit = 50) {
    const logs = await this.prisma.alertLog.findMany({
      where: { organizationId },
      orderBy: { createdAt: 'desc' },
      take: limit,
      include: { channel: { select: { name: true, channel: true } } },
    });
    return logs.map((l) => ({
      id: l.id,
      trigger: l.trigger,
      status: l.status,
      message: l.message,
      channel: l.channel,
      createdAt: l.createdAt.toISOString(),
    }));
  }
}
