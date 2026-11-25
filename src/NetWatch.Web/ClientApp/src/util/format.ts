// Formatting helpers. All timestamps from the server are UTC; they are shown in Pakistan Standard Time.

export const TIME_ZONE = 'Asia/Karachi';

const dateTimeFmt = new Intl.DateTimeFormat('en-GB', {
  timeZone: TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
});

const timeFmt = new Intl.DateTimeFormat('en-GB', {
  timeZone: TIME_ZONE,
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
});

const shortFmt = new Intl.DateTimeFormat('en-GB', {
  timeZone: TIME_ZONE,
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
});

function toDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') return null;
  if (value instanceof Date) return value;
  if (typeof value === 'number') return new Date(value);
  // Server timestamps are UTC; add "Z" if the serializer omitted it.
  const s = /[zZ]|[+-]\d\d:\d\d$/.test(value) ? value : `${value}Z`;
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? null : d;
}

/** Milliseconds since epoch for a server timestamp, or NaN. */
export function toMs(value: string | number | Date | null | undefined): number {
  return toDate(value)?.getTime() ?? Number.NaN;
}

export function fmtDateTime(value: string | number | Date | null | undefined): string {
  const d = toDate(value);
  return d ? dateTimeFmt.format(d).replace(',', '') : '—';
}

export function fmtTime(value: string | number | Date | null | undefined): string {
  const d = toDate(value);
  return d ? timeFmt.format(d) : '—';
}

export function fmtShort(value: string | number | Date | null | undefined): string {
  const d = toDate(value);
  return d ? shortFmt.format(d) : '—';
}

/** "3m ago", "2h 5m ago" ... */
export function fmtAgo(value: string | number | Date | null | undefined, now = Date.now()): string {
  const d = toDate(value);
  if (!d) return '—';
  return `${fmtDuration((now - d.getTime()) / 1000)} ago`;
}

export function fmtDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return '—';
  const s = Math.max(0, Math.floor(seconds));
  const days = Math.floor(s / 86400);
  const hours = Math.floor((s % 86400) / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  if (days > 0) return `${days}d ${hours}h`;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m`;
  return `${s}s`;
}

export function fmtBps(bps: number | null | undefined): string {
  if (bps === null || bps === undefined || !Number.isFinite(bps)) return '—';
  const units = ['bps', 'Kbps', 'Mbps', 'Gbps', 'Tbps'];
  let v = bps;
  let i = 0;
  while (v >= 1000 && i < units.length - 1) {
    v /= 1000;
    i++;
  }
  return `${v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2)} ${units[i]}`;
}

export function fmtSpeed(bps: number | null | undefined): string {
  if (!bps) return '—';
  if (bps >= 1e9) return `${+(bps / 1e9).toFixed(1)} G`;
  if (bps >= 1e6) return `${+(bps / 1e6).toFixed(0)} M`;
  return `${+(bps / 1e3).toFixed(0)} K`;
}

export function fmtBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

export function fmtPct(v: number | null | undefined, digits = 0): string {
  return v === null || v === undefined || !Number.isFinite(v) ? '—' : `${v.toFixed(digits)}%`;
}

export function fmtMs(v: number | null | undefined): string {
  return v === null || v === undefined || !Number.isFinite(v) ? '—' : `${v < 10 ? v.toFixed(1) : v.toFixed(0)} ms`;
}

export function fmtNum(v: number | null | undefined, digits = 1): string {
  return v === null || v === undefined || !Number.isFinite(v) ? '—' : v.toFixed(digits);
}

export function ifStatusText(status: number): string {
  switch (status) {
    case 1:
      return 'up';
    case 2:
      return 'down';
    case 3:
      return 'testing';
    case 5:
      return 'dormant';
    case 6:
      return 'not present';
    case 7:
      return 'lower layer down';
    default:
      return 'unknown';
  }
}

export function typeLabel(type: string): string {
  switch (type) {
    case 'DomainController':
      return 'Domain controller';
    case 'VM':
      return 'Virtual machine';
    case 'VCenter':
      return 'vCenter';
    case 'NetworkOs':
      return 'Network OS';
    case 'Workstation':
      return 'Workstation';
    case 'Esxi':
      return 'ESXi';
    default:
      return type;
  }
}
