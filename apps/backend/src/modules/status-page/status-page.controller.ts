import { Controller, Get, Param } from '@nestjs/common';
import { ApiTags, ApiOperation } from '@nestjs/swagger';
import { Public } from '../../common/decorators/public.decorator';
import { StatusPageService } from './status-page.service';

@ApiTags('Public Status Page')
@Controller('public/status')
export class StatusPageController {
  constructor(private readonly statusPageService: StatusPageService) {}

  @Public()
  @Get(':slug')
  @ApiOperation({ summary: 'Get public status page by organization slug' })
  getStatus(@Param('slug') slug: string) {
    return this.statusPageService.getPublicStatus(slug);
  }
}
