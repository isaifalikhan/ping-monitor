import { Controller, Get, Post, Body, Param } from '@nestjs/common';
import { ApiTags, ApiBearerAuth, ApiOperation } from '@nestjs/swagger';
import { UserRole } from '@netwatch/shared';
import { AlertsService } from './alerts.service';
import { CreateAlertChannelDto } from './dto/create-channel.dto';
import { CreateAlertRuleDto } from './dto/create-rule.dto';
import { OrgId } from '../../common/decorators/org-id.decorator';
import { CurrentUser } from '../../common/decorators/current-user.decorator';
import { Roles } from '../../common/decorators/roles.decorator';

@ApiTags('Alerts')
@ApiBearerAuth()
@Controller('alerts')
export class AlertsController {
  constructor(private readonly alertsService: AlertsService) {}

  @Get('channels')
  @ApiOperation({ summary: 'List alert channels' })
  getChannels(@OrgId() organizationId: string) {
    return this.alertsService.getChannels(organizationId);
  }

  @Post('channels')
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Create alert channel' })
  createChannel(
    @OrgId() organizationId: string,
    @CurrentUser('email') actor: string,
    @Body() dto: CreateAlertChannelDto,
  ) {
    return this.alertsService.createChannel(organizationId, dto, actor);
  }

  @Get('rules')
  @ApiOperation({ summary: 'List alert rules' })
  getRules(@OrgId() organizationId: string) {
    return this.alertsService.getRules(organizationId);
  }

  @Post('rules')
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Create alert rule' })
  createRule(@OrgId() organizationId: string, @Body() dto: CreateAlertRuleDto) {
    return this.alertsService.createRule(organizationId, dto);
  }

  @Post('channels/:id/test')
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Send test alert' })
  testAlert(@OrgId() organizationId: string, @Param('id') id: string) {
    return this.alertsService.testAlert(organizationId, id);
  }

  @Get('logs')
  @ApiOperation({ summary: 'List alert logs' })
  getLogs(@OrgId() organizationId: string) {
    return this.alertsService.getLogs(organizationId);
  }

  @Get('deliveries')
  @ApiOperation({ summary: 'Alert delivery history' })
  getDeliveries(@OrgId() organizationId: string) {
    return this.alertsService.getDeliveries(organizationId);
  }

  @Get('stats')
  @ApiOperation({ summary: 'Alert delivery statistics' })
  getStats(@OrgId() organizationId: string) {
    return this.alertsService.getStats(organizationId);
  }
}
