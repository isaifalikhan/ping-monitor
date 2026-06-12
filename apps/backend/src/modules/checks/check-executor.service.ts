import { Injectable, Logger } from '@nestjs/common';
import { MonitorType } from '@prisma/client';
import * as net from 'net';
import * as tls from 'tls';
import * as dns from 'dns/promises';

export interface CheckResult {
  status: 'UP' | 'DOWN' | 'DEGRADED';
  responseTime: number | null;
  errorMessage?: string;
}

@Injectable()
export class CheckExecutorService {
  private readonly logger = new Logger(CheckExecutorService.name);

  async execute(
    type: MonitorType,
    target: string,
    timeoutSec: number,
    latencyThresholdMs = 500,
  ): Promise<CheckResult> {
    try {
      switch (type) {
        case MonitorType.HTTP:
        case MonitorType.HTTPS:
        case MonitorType.API:
          return await this.httpCheck(target, timeoutSec, latencyThresholdMs, type !== MonitorType.HTTP);
        case MonitorType.TCP:
          return await this.tcpCheck(target, timeoutSec);
        case MonitorType.DNS:
          return await this.dnsCheck(target, timeoutSec);
        case MonitorType.SSL:
          return await this.sslCheck(target, timeoutSec);
        case MonitorType.PING:
          return await this.tcpCheck(target.includes(':') ? target : `${target}:80`, timeoutSec);
        default:
          return await this.httpCheck(target, timeoutSec, latencyThresholdMs, true);
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Check failed';
      this.logger.warn(`Check failed for ${target}: ${message}`);
      return { status: 'DOWN', responseTime: null, errorMessage: message };
    }
  }

  private async httpCheck(
    target: string,
    timeoutSec: number,
    latencyThresholdMs: number,
    forceHttps: boolean,
  ): Promise<CheckResult> {
    const url = target.startsWith('http') ? target : `${forceHttps ? 'https' : 'http'}://${target}`;
    const start = Date.now();
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutSec * 1000);

    try {
      const res = await fetch(url, {
        method: 'GET',
        signal: controller.signal,
        redirect: 'follow',
      });
      const responseTime = Date.now() - start;
      if (!res.ok) {
        return {
          status: 'DOWN',
          responseTime,
          errorMessage: `HTTP ${res.status}`,
        };
      }
      return {
        status: responseTime > latencyThresholdMs ? 'DEGRADED' : 'UP',
        responseTime,
      };
    } finally {
      clearTimeout(timer);
    }
  }

  private tcpCheck(target: string, timeoutSec: number): Promise<CheckResult> {
    return new Promise((resolve) => {
      const [host, portStr] = target.includes(':') ? target.split(':') : [target, '80'];
      const port = parseInt(portStr, 10);
      const start = Date.now();

      const socket = net.connect({ host, port, timeout: timeoutSec * 1000 }, () => {
        const responseTime = Date.now() - start;
        socket.destroy();
        resolve({ status: 'UP', responseTime });
      });

      socket.on('error', (err) => {
        socket.destroy();
        resolve({ status: 'DOWN', responseTime: null, errorMessage: err.message });
      });

      socket.on('timeout', () => {
        socket.destroy();
        resolve({ status: 'DOWN', responseTime: null, errorMessage: 'Connection timeout' });
      });
    });
  }

  private async dnsCheck(target: string, timeoutSec: number): Promise<CheckResult> {
    const start = Date.now();
    const timeout = new Promise<never>((_, reject) =>
      setTimeout(() => reject(new Error('DNS timeout')), timeoutSec * 1000),
    );
    await Promise.race([dns.lookup(target), timeout]);
    const responseTime = Date.now() - start;
    return { status: 'UP', responseTime };
  }

  private sslCheck(host: string, timeoutSec: number): Promise<CheckResult> {
    const hostname = host.replace(/^https?:\/\//, '').split('/')[0].split(':')[0];
    return new Promise((resolve) => {
      const start = Date.now();
      const socket = tls.connect(
        { host: hostname, port: 443, servername: hostname, timeout: timeoutSec * 1000 },
        () => {
          const cert = socket.getPeerCertificate();
          const responseTime = Date.now() - start;
          socket.destroy();
          if (!cert?.valid_to) {
            resolve({ status: 'UP', responseTime });
            return;
          }
          const daysLeft = (new Date(cert.valid_to).getTime() - Date.now()) / 86_400_000;
          if (daysLeft < 14) {
            resolve({
              status: 'DEGRADED',
              responseTime,
              errorMessage: `Certificate expires in ${Math.floor(daysLeft)} days`,
            });
            return;
          }
          resolve({ status: 'UP', responseTime });
        },
      );
      socket.on('error', (err) => {
        socket.destroy();
        resolve({ status: 'DOWN', responseTime: null, errorMessage: err.message });
      });
    });
  }
}
