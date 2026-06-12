import { Controller, Get, Patch, Body } from '@nestjs/common';
import { ApiTags, ApiBearerAuth, ApiOperation } from '@nestjs/swagger';
import { UserRole } from '@netwatch/shared';
import { SettingsService } from './settings.service';
import { UpdateSettingsDto } from './dto/update-settings.dto';
import { OrgId } from '../../common/decorators/org-id.decorator';
import { CurrentUser } from '../../common/decorators/current-user.decorator';
import { Roles } from '../../common/decorators/roles.decorator';

@ApiTags('Settings')
@ApiBearerAuth()
@Controller('settings')
export class SettingsController {
  constructor(private readonly settingsService: SettingsService) {}

  @Get()
  @ApiOperation({ summary: 'Get organization settings' })
  getSettings(@OrgId() organizationId: string) {
    return this.settingsService.getSettings(organizationId);
  }

  @Patch()
  @Roles(UserRole.OWNER)
  @ApiOperation({ summary: 'Update organization settings' })
  updateSettings(
    @OrgId() organizationId: string,
    @CurrentUser('role') role: UserRole,
    @CurrentUser('email') actor: string,
    @Body() dto: UpdateSettingsDto,
  ) {
    return this.settingsService.updateSettings(organizationId, role, dto, actor);
  }
}
