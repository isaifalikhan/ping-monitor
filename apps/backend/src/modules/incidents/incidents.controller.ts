import { Controller, Get, Patch, Param, Query } from '@nestjs/common';
import { ApiTags, ApiBearerAuth, ApiOperation } from '@nestjs/swagger';
import { IncidentStatus } from '@prisma/client';
import { UserRole } from '@netwatch/shared';
import { IncidentsService } from './incidents.service';
import { OrgId } from '../../common/decorators/org-id.decorator';
import { CurrentUser } from '../../common/decorators/current-user.decorator';
import { Roles } from '../../common/decorators/roles.decorator';

@ApiTags('Incidents')
@ApiBearerAuth()
@Controller('incidents')
export class IncidentsController {
  constructor(private readonly incidentsService: IncidentsService) {}

  @Get()
  @ApiOperation({ summary: 'List incidents' })
  findAll(@OrgId() organizationId: string, @Query('status') status?: IncidentStatus) {
    return this.incidentsService.findAll(organizationId, status);
  }

  @Patch(':id/acknowledge')
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Acknowledge incident' })
  acknowledge(
    @OrgId() organizationId: string,
    @CurrentUser('sub') userId: string,
    @Param('id') id: string,
  ) {
    return this.incidentsService.acknowledge(organizationId, id, userId);
  }

  @Patch(':id/resolve')
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Resolve incident' })
  resolve(@OrgId() organizationId: string, @Param('id') id: string) {
    return this.incidentsService.resolve(organizationId, id);
  }
}
