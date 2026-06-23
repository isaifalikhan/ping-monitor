import type { MonitorType } from '@netwatch/shared';
import { resolveCheckTarget } from '@/lib/monitoring/target';

export interface CheckResult {
  status: 'UP' | 'DOWN' | 'DEGRADED';
  responseTime: number | null;
  errorMessage?: string;
}

const SERVERLESS_TYPES = new Set(['HTTP', 'HTTPS', 'API', 'TCP', 'DNS', 'SSL', 'PING']);
const SSL_EXPIRY_WARNING_DAYS = 14;

export function isServerlessCheckType(type: string) {
  return SERVERLESS_TYPES.has(type);
}

function applyLatencyDegradation(
  result: CheckResult,
  latencyThresholdMs: number,
): CheckResult {
  if (
    result.status === 'UP' &&
    result.responseTime != null &&
    result.responseTime > latencyThresholdMs
  ) {
    return {
      status: 'DEGRADED',
      responseTime: result.responseTime,
      errorMessage: `Response time ${result.responseTime}ms exceeds ${latencyThresholdMs}ms threshold`,
    };
  }
  return result;
}

export async function executeCheck(
  type: MonitorType | string,
  target: string,
  timeoutSec: number,
  latencyThresholdMs = 1000,
  port?: number | null,
): Promise<CheckResult> {
  const resolvedTarget = resolveCheckTarget(String(type), target, port);
  try {
    let result: CheckResult;
    switch (type) {
      case 'HTTP':
      case 'HTTPS':
      case 'API':
        result = await httpCheck(resolvedTarget, timeoutSec, type !== 'HTTP');
        break;
      case 'TCP':
      case 'PING':
        result = await tcpCheck(resolvedTarget, timeoutSec);
        break;
      case 'DNS':
        result = await dnsCheck(resolvedTarget, timeoutSec);
        break;
      case 'SSL':
        result = await sslCheck(resolvedTarget, timeoutSec);
        break;
      default:
        return { status: 'DOWN', responseTime: null, errorMessage: `Unsupported type: ${type}` };
    }
    if (type === 'SSL' && result.status === 'DEGRADED') return result;
    return applyLatencyDegradation(result, latencyThresholdMs);
  } catch (err) {
    return {
      status: 'DOWN',
      responseTime: null,
      errorMessage: err instanceof Error ? err.message : 'Check failed',
    };
  }
}

async function httpCheck(
  target: string,
  timeoutSec: number,
  forceHttps: boolean,
): Promise<CheckResult> {
  const url = target.startsWith('http') ? target : `${forceHttps ? 'https' : 'http'}://${target}`;
  const start = Date.now();
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutSec * 1000);
  try {
    const res = await fetch(url, { method: 'GET', signal: controller.signal, redirect: 'follow' });
    const responseTime = Date.now() - start;
    if (!res.ok) return { status: 'DOWN', responseTime, errorMessage: `HTTP ${res.status}` };
    return { status: 'UP', responseTime };
  } catch (err) {
    const responseTime = Date.now() - start;
    return {
      status: 'DOWN',
      responseTime: responseTime > 0 ? responseTime : null,
      errorMessage: err instanceof Error ? err.message : 'HTTP request failed',
    };
  } finally {
    clearTimeout(timer);
  }
}

async function tcpCheck(target: string, timeoutSec: number): Promise<CheckResult> {
  const start = Date.now();
  const [host, portStr] = target.includes(':') ? target.split(':') : [target, '80'];
  const port = parseInt(portStr, 10) || 80;
  const { connect } = await import('net');
  return new Promise((resolve) => {
    const socket = connect({ host, port, timeout: timeoutSec * 1000 }, () => {
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

async function dnsCheck(target: string, timeoutSec: number): Promise<CheckResult> {
  const start = Date.now();
  const dns = await import('dns/promises');
  const timer = new Promise<never>((_, reject) =>
    setTimeout(() => reject(new Error('DNS timeout')), timeoutSec * 1000),
  );
  try {
    await Promise.race([dns.lookup(target.replace(/^https?:\/\//, '').split('/')[0]), timer]);
    return { status: 'UP', responseTime: Date.now() - start };
  } catch (err) {
    return {
      status: 'DOWN',
      responseTime: null,
      errorMessage: err instanceof Error ? err.message : 'DNS lookup failed',
    };
  }
}

async function sslCheck(target: string, timeoutSec: number): Promise<CheckResult> {
  const start = Date.now();
  const [hostPart, portStr] = target.includes(':') ? target.split(':') : [target, '443'];
  const host = hostPart.replace(/^https?:\/\//, '').split('/')[0];
  const port = parseInt(portStr, 10) || 443;
  const tls = await import('tls');
  return new Promise((resolve) => {
    const socket = tls.connect(
      { host, port, servername: host, timeout: timeoutSec * 1000 },
      () => {
        const cert = socket.getPeerCertificate();
        const responseTime = Date.now() - start;
        socket.end();

        if (cert?.valid_to) {
          const daysLeft = (new Date(cert.valid_to).getTime() - Date.now()) / 86_400_000;
          if (daysLeft < 0) {
            resolve({
              status: 'DOWN',
              responseTime,
              errorMessage: 'SSL certificate has expired',
            });
            return;
          }
          if (daysLeft < SSL_EXPIRY_WARNING_DAYS) {
            resolve({
              status: 'DEGRADED',
              responseTime,
              errorMessage: `Certificate expires in ${Math.floor(daysLeft)} days`,
            });
            return;
          }
        }

        resolve({ status: 'UP', responseTime });
      },
    );
    socket.on('error', (err) => {
      socket.destroy();
      resolve({ status: 'DOWN', responseTime: null, errorMessage: err.message });
    });
    socket.on('timeout', () => {
      socket.destroy();
      resolve({ status: 'DOWN', responseTime: null, errorMessage: 'SSL handshake timeout' });
    });
  });
}
