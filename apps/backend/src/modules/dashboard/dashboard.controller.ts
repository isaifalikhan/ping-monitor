import { Controller, Get } from '@nestjs/common';
import { ApiTags, ApiBearerAuth, ApiOperation } from '@nestjs/swagger';
import { DashboardService } from './dashboard.service';
import { OrgId } from '../../common/decorators/org-id.decorator';

@ApiTags('Dashboard')
@ApiBearerAuth()
@Controller('dashboard')
export class DashboardController {
  constructor(private readonly dashboardService: DashboardService) {}

  @Get('stats')
  @ApiOperation({ summary: 'Dashboard statistics' })
  getStats(@OrgId() organizationId: string) {
    return this.dashboardService.getStats(organizationId);
  }

  @Get('charts')
  @ApiOperation({ summary: 'Dashboard chart data' })
  getCharts(@OrgId() organizationId: string) {
    return this.dashboardService.getCharts(organizationId);
  }

  @Get('recent-incidents')
  @ApiOperation({ summary: 'Recent incidents feed' })
  getRecentIncidents(@OrgId() organizationId: string) {
    return this.dashboardService.getRecentIncidents(organizationId);
  }

  @Get('recent-checks')
  @ApiOperation({ summary: 'Recent monitor checks feed' })
  getRecentChecks(@OrgId() organizationId: string) {
    return this.dashboardService.getRecentChecks(organizationId);
  }

  @Get('recent-alerts')
  @ApiOperation({ summary: 'Recent alerts feed' })
  getRecentAlerts(@OrgId() organizationId: string) {
    return this.dashboardService.getRecentAlerts(organizationId);
  }
}
