import { Controller, Get, Patch, Delete, Body, Param } from '@nestjs/common';
import { ApiTags, ApiOperation, ApiBearerAuth } from '@nestjs/swagger';
import { OrganizationsService } from './organizations.service';
import { UpdateOrganizationDto } from './dto/update-organization.dto';
import { UpdateMemberDto } from './dto/update-member.dto';
import { CurrentUser } from '../../common/decorators/current-user.decorator';
import { Roles } from '../../common/decorators/roles.decorator';
import { UserRole } from '@netwatch/shared';

@ApiTags('Organizations')
@ApiBearerAuth()
@Controller('organizations')
export class OrganizationsController {
  constructor(private readonly organizationsService: OrganizationsService) {}

  @Get('current')
  @ApiOperation({ summary: 'Get current organization' })
  async getCurrent(@CurrentUser('organizationId') organizationId: string) {
    return this.organizationsService.getOrganization(organizationId);
  }

  @Patch('current')
  @Roles(UserRole.OWNER)
  @ApiOperation({ summary: 'Update current organization' })
  async updateCurrent(
    @CurrentUser('organizationId') organizationId: string,
    @CurrentUser('role') role: UserRole,
    @Body() dto: UpdateOrganizationDto,
  ) {
    return this.organizationsService.updateOrganization(organizationId, role, dto);
  }

  @Get('team')
  @ApiOperation({ summary: 'Get team members and pending invitations' })
  async getTeam(@CurrentUser('organizationId') organizationId: string) {
    return this.organizationsService.getTeamMembers(organizationId);
  }

  @Patch('team/:memberId')
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Update team member role or status' })
  async updateMember(
    @CurrentUser('organizationId') organizationId: string,
    @CurrentUser('sub') actorId: string,
    @CurrentUser('role') actorRole: UserRole,
    @Param('memberId') memberId: string,
    @Body() dto: UpdateMemberDto,
  ) {
    return this.organizationsService.updateMember(
      organizationId,
      memberId,
      actorRole,
      actorId,
      dto,
    );
  }

  @Delete('team/:memberId')
  @Roles(UserRole.ADMIN)
  @ApiOperation({ summary: 'Remove team member' })
  async removeMember(
    @CurrentUser('organizationId') organizationId: string,
    @CurrentUser('sub') actorId: string,
    @CurrentUser('role') actorRole: UserRole,
    @Param('memberId') memberId: string,
  ) {
    return this.organizationsService.removeMember(
      organizationId,
      memberId,
      actorRole,
      actorId,
    );
  }
}
