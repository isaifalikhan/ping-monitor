import {
  Controller,
  Get,
  Post,
  Patch,
  Delete,
  Body,
  Param,
  Query,
} from '@nestjs/common';
import { ApiTags, ApiBearerAuth, ApiOperation } from '@nestjs/swagger';
import { UserRole } from '@netwatch/shared';
import { MonitorsService } from './monitors.service';
import { CreateMonitorDto } from './dto/create-monitor.dto';
import { UpdateMonitorDto } from './dto/update-monitor.dto';
import { QueryMonitorsDto } from './dto/query-monitors.dto';
import { OrgId } from '../../common/decorators/org-id.decorator';
import { CurrentUser } from '../../common/decorators/current-user.decorator';
import { Roles } from '../../common/decorators/roles.decorator';

@ApiTags('Monitors')
@ApiBearerAuth()
@Controller('monitors')
export class MonitorsController {
  constructor(private readonly monitorsService: MonitorsService) {}

  @Get()
  @ApiOperation({ summary: 'List monitors' })
  findAll(@OrgId() organizationId: string, @Query() query: QueryMonitorsDto) {
    return this.monitorsService.findAll(organizationId, query);
  }

  @Get(':id')
  @ApiOperation({ summary: 'Get monitor detail' })
  findOne(@OrgId() organizationId: string, @Param('id') id: string) {
    return this.monitorsService.getDetail(organizationId, id);
  }

  @Post()
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Create monitor' })
  create(
    @OrgId() organizationId: string,
    @CurrentUser('email') actor: string,
    @Body() dto: CreateMonitorDto,
  ) {
    return this.monitorsService.create(organizationId, dto, actor);
  }

  @Patch(':id')
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Update monitor' })
  update(
    @OrgId() organizationId: string,
    @CurrentUser('role') role: UserRole,
    @CurrentUser('email') actor: string,
    @Param('id') id: string,
    @Body() dto: UpdateMonitorDto,
  ) {
    return this.monitorsService.update(organizationId, id, dto, role, actor);
  }

  @Delete(':id')
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Delete monitor' })
  remove(
    @OrgId() organizationId: string,
    @CurrentUser('role') role: UserRole,
    @CurrentUser('email') actor: string,
    @Param('id') id: string,
  ) {
    return this.monitorsService.remove(organizationId, id, role, actor);
  }
}
