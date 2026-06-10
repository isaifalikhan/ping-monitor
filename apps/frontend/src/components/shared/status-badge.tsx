import { Badge } from '@/components/ui/badge';

const STATUS_MAP: Record<string, 'success' | 'danger' | 'warning' | 'muted' | 'default'> = {
  UP: 'success',
  ONLINE: 'success',
  DOWN: 'danger',
  OFFLINE: 'danger',
  DEGRADED: 'warning',
  UNKNOWN: 'muted',
  MAINTENANCE: 'warning',
  OPEN: 'danger',
  ACKNOWLEDGED: 'warning',
  RESOLVED: 'success',
  SENT: 'success',
  FAILED: 'danger',
  PENDING: 'warning',
  LOW: 'muted',
  MEDIUM: 'warning',
  HIGH: 'danger',
  CRITICAL: 'danger',
};

export function StatusBadge({ status }: { status: string }) {
  const variant = STATUS_MAP[status] ?? 'default';
  const label = status.charAt(0) + status.slice(1).toLowerCase();
  return <Badge variant={variant}>{label}</Badge>;
}
