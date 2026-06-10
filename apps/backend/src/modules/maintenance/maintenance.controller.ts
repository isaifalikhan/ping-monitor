import { Controller, Get, Post, Body } from '@nestjs/common';
import { ApiTags, ApiBearerAuth, ApiOperation } from '@nestjs/swagger';
import { UserRole } from '@netwatch/shared';
import { MaintenanceService } from './maintenance.service';
import { CreateMaintenanceDto } from './dto/create-maintenance.dto';
import { OrgId } from '../../common/decorators/org-id.decorator';
import { Roles } from '../../common/decorators/roles.decorator';

@ApiTags('Maintenance')
@ApiBearerAuth()
@Controller('maintenance')
export class MaintenanceController {
  constructor(private readonly maintenanceService: MaintenanceService) {}

  @Get()
  @ApiOperation({ summary: 'List maintenance windows' })
  findAll(@OrgId() organizationId: string) {
    return this.maintenanceService.findAll(organizationId);
  }

  @Post()
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Create maintenance window' })
  create(@OrgId() organizationId: string, @Body() dto: CreateMaintenanceDto) {
    return this.maintenanceService.create(organizationId, dto);
  }
}
