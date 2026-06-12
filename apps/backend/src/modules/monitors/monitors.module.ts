import { Module } from '@nestjs/common';
import { MonitorsController } from './monitors.controller';
import { MonitorsService } from './monitors.service';
import { ChecksModule } from '../checks/checks.module';

@Module({
  imports: [ChecksModule],
  controllers: [MonitorsController],
  providers: [MonitorsService],
  exports: [MonitorsService],
})
export class MonitorsModule {}
