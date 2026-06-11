export interface Monitor {
  id: string;
  name: string;
  description?: string | null;
  type: string;
  target: string;
  status: string;
  tags: string[];
  group?: string | null;
  checkInterval: number;
  timeout: number;
  retryCount: number;
  isActive: boolean;
  maintenanceMode: boolean;
  lastResponseTime?: number | null;
  lastCheckedAt?: string | null;
  uptimePercent: number;
  createdAt: string;
  updatedAt: string;
}

export interface MonitorCheck {
  id: string;
  status: string;
  responseTime?: number | null;
  packetLoss?: number | null;
  errorMessage?: string | null;
  createdAt: string;
}

export interface IncidentTimelineEvent {
  id: string;
  type: 'DETECTED' | 'ACKNOWLEDGED' | 'ESCALATED' | 'NOTE' | 'RESOLVED';
  message: string;
  user?: string;
  createdAt: string;
}

export interface Incident {
  id: string;
  title?: string;
  monitor: { id: string; name: string; target?: string };
  status: string;
  severity: string;
  startedAt: string;
  endedAt?: string | null;
  duration?: number | null;
  rootCause?: string | null;
  notes?: string | null;
  assignedUser?: { id: string; firstName: string; lastName: string; email?: string } | null;
  timeline?: IncidentTimelineEvent[];
}

export interface AlertChannel {
  id: string;
  channel: string;
  name: string;
  config: Record<string, unknown>;
  isActive: boolean;
  rulesCount?: number;
  successRate?: number;
  deliveryCount?: number;
  createdAt: string;
}

export interface AlertDelivery {
  id: string;
  channel: string;
  channelName: string;
  monitor: string;
  trigger: string;
  status: 'SENT' | 'FAILED' | 'PENDING';
  message?: string;
  createdAt: string;
}

export interface AlertRule {
  id: string;
  trigger: string;
  isActive: boolean;
  channel: { id: string; name: string; channel: string };
  monitor?: { id: string; name: string } | null;
}

export interface MaintenanceWindow {
  id: string;
  title: string;
  monitor?: { id: string; name: string } | null;
  startTime: string;
  endTime: string;
  reason?: string | null;
  pauseAlerts: boolean;
  status: 'UPCOMING' | 'ACTIVE' | 'COMPLETED';
  createdAt: string;
}

export interface TeamMember {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  role: string;
  title?: string;
  emailVerified: boolean;
  isActive: boolean;
  lastLoginAt?: string | null;
  createdAt: string;
  avatarColor?: string;
  status?: 'online' | 'away' | 'offline';
}

export interface AuditLogEntry {
  id: string;
  category: 'USER' | 'MONITOR' | 'ALERT' | 'SECURITY' | 'SETTINGS' | 'INCIDENT';
  action: string;
  actor: string;
  target?: string;
  ipAddress?: string;
  createdAt: string;
}

export interface StatusPageComponent {
  id: string;
  name: string;
  status: 'UP' | 'DEGRADED' | 'DOWN' | 'MAINTENANCE';
  uptime90d: number;
}

export interface Paginated<T> {
  items: T[];
  total: number;
  page: number;
  limit: number;
  totalPages: number;
}
