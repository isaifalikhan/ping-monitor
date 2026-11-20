import type { DeviceStatus, Severity } from '../api/types';

export function StatusBadge({ status }: { status: DeviceStatus }) {
  return (
    <span className={`badge s-${status.toLowerCase()}`}>
      <span className={`dot s-${status.toLowerCase()}`} />
      {status}
    </span>
  );
}

export function StatusDot({ status, title }: { status: DeviceStatus; title?: string }) {
  return <span className={`dot s-${status.toLowerCase()}`} title={title ?? status} />;
}

export function SeverityBadge({ severity }: { severity: Severity }) {
  return <span className={`badge sev-${severity.toLowerCase()}`}>{severity}</span>;
}

export function UtilBar({ value }: { value: number | null | undefined }) {
  if (value === null || value === undefined) return <span className="faint">—</span>;
  const cls = value >= 90 ? 'crit' : value >= 70 ? 'warn' : '';
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
      <div className={`bar ${cls}`} style={{ flex: 1 }}>
        <div style={{ width: `${Math.min(100, Math.max(0, value))}%` }} />
      </div>
      <span className="num" style={{ width: 40 }}>
        {value.toFixed(0)}%
      </span>
    </div>
  );
}
