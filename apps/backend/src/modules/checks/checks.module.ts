import { Module } from '@nestjs/common';
import { CheckExecutorService } from './check-executor.service';
import { ChecksService } from './checks.service';
import { ChecksScheduler } from './checks.scheduler';

@Module({
  providers: [CheckExecutorService, ChecksService, ChecksScheduler],
  exports: [ChecksService, ChecksScheduler],
})
export class ChecksModule {}
