import { Controller, Get, Query } from '@nestjs/common';
import { ApiTags, ApiBearerAuth, ApiOperation } from '@nestjs/swagger';
import { AuditLogCategory } from '@prisma/client';
import { AuditService } from './audit.service';
import { OrgId } from '../../common/decorators/org-id.decorator';

@ApiTags('Audit Logs')
@ApiBearerAuth()
@Controller('audit-logs')
export class AuditController {
  constructor(private readonly auditService: AuditService) {}

  @Get()
  @ApiOperation({ summary: 'List audit logs' })
  findAll(
    @OrgId() organizationId: string,
    @Query('category') category?: AuditLogCategory,
  ) {
    return this.auditService.findAll(organizationId, category);
  }
}
