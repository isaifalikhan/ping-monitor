import { Module } from '@nestjs/common';
import { StatusPageService } from './status-page.service';
import { StatusPageController } from './status-page.controller';

@Module({
  controllers: [StatusPageController],
  providers: [StatusPageService],
})
export class StatusPageModule {}
