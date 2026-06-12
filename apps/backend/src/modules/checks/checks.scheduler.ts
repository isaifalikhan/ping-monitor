import { Injectable, Logger, OnModuleDestroy, OnModuleInit } from '@nestjs/common';
import { ConfigService } from '@nestjs/config';
import { Queue, Worker } from 'bullmq';
import { ChecksService } from './checks.service';

const QUEUE_NAME = 'monitor-checks';
const TICK_JOB = 'tick';

@Injectable()
export class ChecksScheduler implements OnModuleInit, OnModuleDestroy {
  private readonly logger = new Logger(ChecksScheduler.name);
  private interval?: NodeJS.Timeout;
  private queue?: Queue;
  private worker?: Worker;

  constructor(
    private readonly checksService: ChecksService,
    private readonly config: ConfigService,
  ) {}

  async onModuleInit() {
    const enabled = this.config.get<string>('CHECK_ENGINE_ENABLED', 'true') !== 'false';
    if (!enabled) {
      this.logger.log('Check engine disabled (CHECK_ENGINE_ENABLED=false)');
      return;
    }

    const useRedis = this.config.get<string>('CHECKS_USE_REDIS', 'false') === 'true';
    if (useRedis) {
      await this.startBullMq();
    } else {
      this.startInProcessScheduler();
    }
  }

  async onModuleDestroy() {
    if (this.interval) clearInterval(this.interval);
    await this.worker?.close();
    await this.queue?.close();
  }

  private startInProcessScheduler() {
    const intervalMs = parseInt(this.config.get<string>('CHECK_TICK_MS', '30000'), 10);
    this.logger.log(`Starting in-process check scheduler (every ${intervalMs}ms)`);

    const tick = () => {
      void this.checksService.runDueChecks().then(({ processed }) => {
        if (processed > 0) {
          this.logger.debug(`Processed ${processed} monitor checks`);
        }
      });
    };

    tick();
    this.interval = setInterval(tick, intervalMs);
  }

  private async startBullMq() {
    const host = this.config.get<string>('REDIS_HOST', 'localhost');
    const port = parseInt(this.config.get<string>('REDIS_PORT', '6379'), 10);
    const password = this.config.get<string>('REDIS_PASSWORD');

    const connection = { host, port, password, maxRetriesPerRequest: null as null };

    this.queue = new Queue(QUEUE_NAME, { connection });
    this.worker = new Worker(
      QUEUE_NAME,
      async (job) => {
        if (job.name === TICK_JOB) {
          return this.checksService.runDueChecks();
        }
        if (job.data?.monitorId) {
          return this.checksService.runCheck(job.data.monitorId);
        }
      },
      { connection },
    );

    await this.queue.add(
      TICK_JOB,
      {},
      { repeat: { every: 30_000 }, removeOnComplete: true, removeOnFail: true },
    );

    this.logger.log('BullMQ check worker started');
  }

  async enqueueMonitorCheck(monitorId: string) {
    if (this.queue) {
      await this.queue.add('check', { monitorId }, { removeOnComplete: true });
    } else {
      await this.checksService.runCheck(monitorId);
    }
  }
}
