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

export interface Incident {
  id: string;
  monitor: { id: string; name: string; target?: string };
  status: string;
  severity: string;
  startedAt: string;
  endedAt?: string | null;
  duration?: number | null;
  rootCause?: string | null;
  notes?: string | null;
  assignedUser?: { id: string; firstName: string; lastName: string; email?: string } | null;
}

export interface AlertChannel {
  id: string;
  channel: string;
  name: string;
  config: Record<string, unknown>;
  isActive: boolean;
  rulesCount?: number;
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
  createdAt: string;
}

export interface TeamMember {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  role: string;
  emailVerified: boolean;
  isActive: boolean;
  lastLoginAt?: string | null;
  createdAt: string;
}

export interface Paginated<T> {
  items: T[];
  total: number;
  page: number;
  limit: number;
  totalPages: number;
}
