/**
 * MOCK DATA ONLY - replace with real API later
 * Centralized in-memory state for demo mode walkthrough.
 */
import { create } from 'zustand';
import type {
  Monitor,
  MonitorCheck,
  Incident,
  AlertChannel,
  AlertRule,
  AlertDelivery,
  MaintenanceWindow,
  TeamMember,
  AuditLogEntry,
} from '@/lib/types';
import { resolveMaintenanceStatus } from '@/lib/maintenance/utils';
import {
  initialMonitors,
  initialMonitorChecks,
  initialMonitorAlertHistory,
  initialIncidents,
  initialAlertChannels,
  initialAlertRules,
  initialAlertDeliveries,
  initialRecentAlerts,
  initialTeamMembers,
  initialPendingInvitations,
  initialMaintenanceWindows,
  initialSettings,
  initialOrganization,
  initialAuditLogs,
} from '@/lib/mock-data';

function uid(prefix: string) {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}

function delay(ms = 300) {
  return new Promise((r) => setTimeout(r, ms));
}

export interface DemoSettings {
  latencyThresholdMs: number;
  notificationEmail: string;
  timezone: string;
  defaultCheckInterval: number;
  defaultTimeout: number;
  retentionDays: number;
  enablePublicStatusPage: boolean;
  statusPageSlug: string;
  sessionTimeoutMinutes: number;
  requireMfa: boolean;
  ipAllowlist: string;
  brandPrimaryColor: string;
  brandLogoUrl: string;
  slackWebhook: string;
  pagerdutyKey: string;
  datadogApiKey: string;
}

interface DemoState {
  monitors: Monitor[];
  monitorChecks: Record<string, MonitorCheck[]>;
  monitorAlertHistory: Record<string, { id: string; trigger: string; status: string; createdAt: string }[]>;
  incidents: Incident[];
  alertChannels: AlertChannel[];
  alertRules: AlertRule[];
  alertDeliveries: AlertDelivery[];
  recentAlerts: typeof initialRecentAlerts;
  auditLogs: AuditLogEntry[];
  maintenanceWindows: MaintenanceWindow[];
  teamMembers: TeamMember[];
  pendingInvitations: { id: string; email: string; role: string; expiresAt: string }[];
  settings: DemoSettings;
  organization: typeof initialOrganization;

  getMonitors: (params?: { search?: string; status?: string; type?: string }) => Monitor[];
  getMonitorDetail: (id: string) => {
    monitor: Monitor;
    checks: MonitorCheck[];
    incidents: Incident[];
    alertHistory: { id: string; trigger: string; status: string; createdAt: string }[];
  } | null;
  addMonitor: (data: Record<string, unknown>) => Monitor;
  updateMonitor: (id: string, data: Record<string, unknown>) => Monitor;
  deleteMonitor: (id: string) => void;

  getIncidents: (status?: string) => Incident[];
  acknowledgeIncident: (id: string, actor?: string) => void;
  resolveIncident: (id: string, actor?: string, data?: { rootCause?: string; notes?: string }) => void;
  updateIncident: (id: string, data: Record<string, unknown>, actor?: string) => void;

  addAlertChannel: (data: { channel: string; name: string; config: Record<string, unknown> }) => AlertChannel;
  addAlertRule: (data: { channelId: string; trigger: string; monitorId?: string }) => AlertRule;
  testAlertChannel: (channelId: string) => { status: string; message: string };

  inviteMember: (data: { email: string; role: string }) => void;
  updateMember: (id: string, data: Record<string, unknown>) => void;
  removeMember: (id: string) => void;

  createMaintenance: (data: Record<string, unknown>) => MaintenanceWindow;
  cancelMaintenance: (id: string) => { id: string; action: 'deleted' | 'ended' };
  updateSettings: (data: Partial<DemoSettings>) => void;

  getDashboardStats: () => {
    totalMonitors: number;
    online: number;
    offline: number;
    degraded: number;
    activeIncidents: number;
    uptimePercent: number;
    avgResponseTime: number;
  };
  getRecentIncidents: () => { id: string; monitor: string; status: string; duration: string; started: string }[];
  getRecentChecks: () => { id: string; monitor: string; status: string; responseTime: number | null; time: string }[];
  getDashboardCharts: () => {
    responseTimeTrend: { time: string; ms: number }[];
    uptimeTrend: { day: string; uptime: number }[];
    uptime24h: { hour: string; uptime: number }[];
    incidentTrend: { day: string; incidents: number }[];
    categoryDistribution: { name: string; value: number; color: string }[];
    monitorDistribution: { name: string; value: number; color: string }[];
    statusDistribution: { name: string; value: number; color: string }[];
  };
  getReportSummary: (from?: string, to?: string) => {
    summary: {
      totalMonitors: number;
      avgUptime: number;
      avgResponseTime: number;
      totalIncidents: number;
      openIncidents: number;
      resolvedIncidents: number;
    };
    responseTrend: { day: string; ms: number }[];
    incidentTrend: { day: string; count: number }[];
    monitors: { name: string; uptime: number; status: string; category: string | null }[];
    period: { from: string; to: string };
  };
}

export const useDemoStore = create<DemoState>((set, get) => ({
  monitors: [...initialMonitors],
  monitorChecks: { ...initialMonitorChecks },
  monitorAlertHistory: { ...initialMonitorAlertHistory },
  incidents: [...initialIncidents],
  alertChannels: [...initialAlertChannels],
  alertRules: [...initialAlertRules],
  alertDeliveries: [...initialAlertDeliveries],
  recentAlerts: [...initialRecentAlerts],
  auditLogs: [...initialAuditLogs],
  maintenanceWindows: [...initialMaintenanceWindows],
  teamMembers: [...initialTeamMembers],
  pendingInvitations: [...initialPendingInvitations],
  settings: { ...initialSettings },
  organization: { ...initialOrganization },

  getMonitors: (params) => {
    let items = get().monitors;
    if (params?.search) {
      const q = params.search.toLowerCase();
      items = items.filter(
        (m) =>
          m.name.toLowerCase().includes(q) ||
          m.target.toLowerCase().includes(q) ||
          m.tags.some((t) => t.toLowerCase().includes(q)),
      );
    }
    if (params?.status) items = items.filter((m) => m.status === params.status);
    if (params?.type) items = items.filter((m) => m.type === params.type);
    return items;
  },

  getMonitorDetail: (id) => {
    const monitor = get().monitors.find((m) => m.id === id);
    if (!monitor) return null;
    return {
      monitor,
      checks: get().monitorChecks[id] ?? [],
      incidents: get().incidents.filter((i) => i.monitor.id === id),
      alertHistory: get().monitorAlertHistory[id] ?? [],
    };
  },

  addMonitor: (data) => {
    const now = new Date().toISOString();
    const monitor: Monitor = {
      id: uid('mon'),
      name: String(data.name),
      description: data.description ? String(data.description) : null,
      type: String(data.type),
      target: String(data.target),
      category: data.category ? String(data.category) : null,
      port: data.port != null ? Number(data.port) : null,
      criticality: data.criticality ? String(data.criticality) : 'MEDIUM',
      deviceLocation: data.deviceLocation ? String(data.deviceLocation) : null,
      rackCabinet: data.rackCabinet ? String(data.rackCabinet) : null,
      switchPort: data.switchPort ? String(data.switchPort) : null,
      deviceOwner: data.deviceOwner ? String(data.deviceOwner) : null,
      vendor: data.vendor ? String(data.vendor) : null,
      model: data.model ? String(data.model) : null,
      ipAddress: data.ipAddress ? String(data.ipAddress) : null,
      status: 'UNKNOWN',
      tags: (data.tags as string[]) ?? [],
      group: data.group ? String(data.group) : null,
      checkInterval: Number(data.checkInterval) || 60,
      timeout: Number(data.timeout) || 30,
      retryCount: Number(data.retryCount) || 3,
      isActive: true,
      maintenanceMode: false,
      lastResponseTime: null,
      lastCheckedAt: null,
      uptimePercent: 100,
      createdAt: now,
      updatedAt: now,
    };
    set((s) => ({
      monitors: [monitor, ...s.monitors],
      monitorChecks: { ...s.monitorChecks, [monitor.id]: [] },
    }));
    return monitor;
  },

  updateMonitor: (id, data) => {
    let updated!: Monitor;
    set((s) => ({
      monitors: s.monitors.map((m) => {
        if (m.id !== id) return m;
        updated = {
          ...m,
          ...data,
          name: data.name != null ? String(data.name) : m.name,
          target: data.target != null ? String(data.target) : m.target,
          type: data.type != null ? String(data.type) : m.type,
          description: data.description !== undefined ? (data.description ? String(data.description) : null) : m.description,
          group: data.group !== undefined ? (data.group ? String(data.group) : null) : m.group,
          tags: data.tags != null ? (data.tags as string[]) : m.tags,
          checkInterval: data.checkInterval != null ? Number(data.checkInterval) : m.checkInterval,
          timeout: data.timeout != null ? Number(data.timeout) : m.timeout,
          retryCount: data.retryCount != null ? Number(data.retryCount) : m.retryCount,
          isActive: data.isActive != null ? Boolean(data.isActive) : m.isActive,
          updatedAt: new Date().toISOString(),
        };
        return updated;
      }),
    }));
    return updated;
  },

  deleteMonitor: (id) => {
    set((s) => ({
      monitors: s.monitors.filter((m) => m.id !== id),
      incidents: s.incidents.filter((i) => i.monitor.id !== id),
    }));
  },

  getIncidents: (status) => {
    const items = get().incidents;
    return status ? items.filter((i) => i.status === status) : items;
  },

  acknowledgeIncident: (id, actor = 'You') => {
    const now = new Date().toISOString();
    set((s) => ({
      incidents: s.incidents.map((i) => {
        if (i.id !== id) return i;
        const events: Incident['timeline'] = [
          {
            id: uid('t'),
            type: 'ACKNOWLEDGED',
            message: `Acknowledged by ${actor}`,
            user: actor,
            createdAt: now,
          },
        ];
        const assignedUser = i.assignedUser ?? {
          id: 'demo-user',
          firstName: actor.split(' ')[0] ?? 'You',
          lastName: actor.split(' ').slice(1).join(' ') || '',
        };
        if (!i.assignedUser) {
          events.push({
            id: uid('t'),
            type: 'ASSIGNED',
            message: `Assigned to ${actor}`,
            user: actor,
            createdAt: now,
          });
        }
        return {
          ...i,
          status: 'ACKNOWLEDGED',
          assignedUser,
          timeline: [...(i.timeline ?? []), ...events],
        };
      }),
    }));
  },

  resolveIncident: (id, actor = 'You', data) => {
    const now = new Date().toISOString();
    set((s) => ({
      incidents: s.incidents.map((i) =>
        i.id === id
          ? {
              ...i,
              status: 'RESOLVED',
              endedAt: now,
              duration: i.duration ?? 0,
              rootCause: data?.rootCause ?? i.rootCause,
              notes: data?.notes ?? i.notes,
              timeline: [
                ...(i.timeline ?? []),
                {
                  id: uid('t'),
                  type: 'RESOLVED' as const,
                  message: `Resolved by ${actor}`,
                  user: actor,
                  createdAt: now,
                },
              ],
            }
          : i,
      ),
    }));
  },

  updateIncident: (id, data, actor = 'You') => {
    const now = new Date().toISOString();
    set((s) => ({
      incidents: s.incidents.map((i) => {
        if (i.id !== id) return i;
        const timeline = [...(i.timeline ?? [])];
        if (data.note) {
          timeline.push({
            id: uid('t'),
            type: 'NOTE',
            message: String(data.note),
            user: actor,
            createdAt: now,
          });
        }
        if (data.assignedUserId && data.assignedUserId !== i.assignedUser?.id) {
          const member = get().teamMembers.find((m) => m.id === data.assignedUserId);
          timeline.push({
            id: uid('t'),
            type: 'ASSIGNED',
            message: `Assigned to ${member ? `${member.firstName} ${member.lastName}` : 'engineer'}`,
            user: actor,
            createdAt: now,
          });
        }
        const member = data.assignedUserId
          ? get().teamMembers.find((m) => m.id === data.assignedUserId)
          : null;
        return {
          ...i,
          severity: data.severity != null ? String(data.severity) : i.severity,
          rootCause: data.rootCause !== undefined ? (data.rootCause ? String(data.rootCause) : null) : i.rootCause,
          notes: data.notes !== undefined ? (data.notes ? String(data.notes) : null) : i.notes,
          assignedUser: data.assignedUserId
            ? member
              ? { id: member.id, firstName: member.firstName, lastName: member.lastName, email: member.email }
              : i.assignedUser
            : data.assignedUserId === null
              ? null
              : i.assignedUser,
          timeline,
        };
      }),
    }));
  },

  addAlertChannel: (data) => {
    const channel: AlertChannel = {
      id: uid('ch'),
      channel: data.channel,
      name: data.name,
      config: data.config,
      isActive: true,
      rulesCount: 0,
      createdAt: new Date().toISOString(),
    };
    set((s) => ({ alertChannels: [channel, ...s.alertChannels] }));
    return channel;
  },

  addAlertRule: (data) => {
    const channel = get().alertChannels.find((c) => c.id === data.channelId);
    const monitor = data.monitorId
      ? get().monitors.find((m) => m.id === data.monitorId)
      : null;
    const rule: AlertRule = {
      id: uid('rule'),
      trigger: data.trigger,
      isActive: true,
      channel: {
        id: channel?.id ?? data.channelId,
        name: channel?.name ?? 'Unknown',
        channel: channel?.channel ?? 'EMAIL',
      },
      monitor: monitor ? { id: monitor.id, name: monitor.name } : null,
    };
    set((s) => ({
      alertRules: [rule, ...s.alertRules],
      alertChannels: s.alertChannels.map((c) =>
        c.id === data.channelId ? { ...c, rulesCount: (c.rulesCount ?? 0) + 1 } : c,
      ),
    }));
    return rule;
  },

  testAlertChannel: (channelId) => {
    const channel = get().alertChannels.find((c) => c.id === channelId);
    if (!channel) return { status: 'error', message: 'Channel not found' };
    const supported = ['EMAIL', 'WEBHOOK', 'SLACK'].includes(channel.channel);
    if (!supported) {
      return { status: 'error', message: `${channel.channel} is not supported yet` };
    }
    return {
      status: 'sent',
      message: `Test alert sent via ${channel.channel} to "${channel.name}" (demo simulation)`,
    };
  },

  inviteMember: (data) => {
    const expiresAt = new Date(Date.now() + 7 * 86_400_000).toISOString();
    set((s) => ({
      pendingInvitations: [
        { id: uid('inv'), email: data.email, role: data.role, expiresAt },
        ...s.pendingInvitations,
      ],
    }));
  },

  updateMember: (id, data) => {
    set((s) => ({
      teamMembers: s.teamMembers.map((m) =>
        m.id === id ? { ...m, ...data, role: data.role != null ? String(data.role) : m.role, isActive: data.isActive != null ? Boolean(data.isActive) : m.isActive } : m,
      ),
    }));
  },

  removeMember: (id) => {
    set((s) => ({ teamMembers: s.teamMembers.filter((m) => m.id !== id) }));
  },

  createMaintenance: (data) => {
    const monitor = data.monitorId
      ? get().monitors.find((m) => m.id === data.monitorId)
      : null;
    const startTime = String(data.startTime);
    const endTime = String(data.endTime);
    const window: MaintenanceWindow = {
      id: uid('mw'),
      title: String(data.title),
      monitor: monitor ? { id: monitor.id, name: monitor.name } : null,
      startTime,
      endTime,
      reason: data.reason ? String(data.reason) : null,
      pauseAlerts: data.pauseAlerts !== false,
      status: resolveMaintenanceStatus(startTime, endTime),
      createdAt: new Date().toISOString(),
    };
    set((s) => ({ maintenanceWindows: [window, ...s.maintenanceWindows] }));
    return window;
  },

  cancelMaintenance: (id) => {
    const existing = get().maintenanceWindows.find((w) => w.id === id);
    if (!existing) throw new Error('Maintenance window not found');
    const status = resolveMaintenanceStatus(existing.startTime, existing.endTime);
    if (status === 'COMPLETED') throw new Error('Completed maintenance windows cannot be cancelled');

    if (status === 'UPCOMING') {
      set((s) => ({
        maintenanceWindows: s.maintenanceWindows.filter((w) => w.id !== id),
      }));
      return { id, action: 'deleted' as const };
    }

    const now = new Date().toISOString();
    set((s) => ({
      maintenanceWindows: s.maintenanceWindows.map((w) =>
        w.id === id ? { ...w, endTime: now, status: 'COMPLETED' as const } : w,
      ),
    }));
    return { id, action: 'ended' as const };
  },

  updateSettings: (data) => {
    set((s) => ({ settings: { ...s.settings, ...data } }));
  },

  getDashboardStats: () => {
    const monitors = get().monitors.filter((m) => m.isActive);
    const incidents = get().incidents;
    const activeIncidents = incidents.filter((i) => i.status !== 'RESOLVED').length;
    const online = monitors.filter((m) => m.status === 'UP').length;
    const offline = monitors.filter((m) => m.status === 'DOWN').length;
    const degraded = monitors.filter((m) => m.status === 'DEGRADED').length;
    const responseTimes = monitors
      .map((m) => m.lastResponseTime)
      .filter((v): v is number => v != null);
    const avgResponseTime =
      responseTimes.length > 0
        ? Math.round(responseTimes.reduce((a, b) => a + b, 0) / responseTimes.length)
        : 0;
    const uptimePercent =
      monitors.length > 0
        ? Math.round((monitors.reduce((a, m) => a + m.uptimePercent, 0) / monitors.length) * 100) / 100
        : 100;

    return {
      totalMonitors: get().monitors.length,
      online,
      offline,
      degraded,
      activeIncidents,
      uptimePercent,
      avgResponseTime,
    };
  },

  getRecentIncidents: () =>
    get()
      .incidents.slice(0, 5)
      .map((i) => ({
        id: i.id,
        monitor: i.monitor.name,
        status: i.status,
        duration: i.duration
          ? i.duration < 3600
            ? `${Math.floor(i.duration / 60)}m`
            : `${Math.floor(i.duration / 3600)}h`
          : '—',
        started: new Date(i.startedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
      })),

  getRecentChecks: () => {
    const checks: { id: string; monitor: string; status: string; responseTime: number | null; time: string; sortKey: number }[] = [];
    for (const m of get().monitors) {
      const latest = get().monitorChecks[m.id]?.[0];
      if (latest) {
        checks.push({
          id: latest.id,
          monitor: m.name,
          status: latest.status,
          responseTime: latest.responseTime ?? null,
          time: new Date(latest.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
          sortKey: new Date(latest.createdAt).getTime(),
        });
      }
    }
    return checks
      .sort((a, b) => b.sortKey - a.sortKey)
      .slice(0, 12)
      .map(({ sortKey: _, ...rest }) => rest);
  },

  getDashboardCharts: () => {
    const monitors = get().monitors.filter((m) => m.isActive);
    const incidents = get().incidents;
    const allChecks = get().monitorChecks;

    const online = monitors.filter((m) => m.status === 'UP').length;
    const offline = monitors.filter((m) => m.status === 'DOWN').length;
    const degraded = monitors.filter((m) => m.status === 'DEGRADED').length;
    const unknown = monitors.filter((m) => m.status === 'UNKNOWN').length;

    const categoryColors: Record<string, string> = {
      ERP: 'hsl(var(--primary))',
      Server: 'hsl(142 76% 36%)',
      Switch: 'hsl(38 92% 50%)',
      Router: 'hsl(199 89% 48%)',
      Firewall: 'hsl(0 72% 51%)',
      Database: 'hsl(280 70% 50%)',
      Website: 'hsl(262 83% 58%)',
      API: 'hsl(173 58% 39%)',
      DNS: 'hsl(45 93% 47%)',
      SSL: 'hsl(215 20% 55%)',
      Other: 'hsl(220 9% 46%)',
    };
    const categoryCounts: Record<string, number> = {};
    for (const m of monitors) {
      const cat = m.category || 'Other';
      categoryCounts[cat] = (categoryCounts[cat] ?? 0) + 1;
    }
    const categoryDistribution = Object.entries(categoryCounts).map(([name, value]) => ({
      name,
      value,
      color: categoryColors[name] ?? categoryColors.Other,
    }));

    const statusDistribution = [
      { name: 'Online', value: online, color: 'hsl(142 76% 36%)' },
      { name: 'Degraded', value: degraded, color: 'hsl(38 92% 50%)' },
      { name: 'Offline', value: offline, color: 'hsl(var(--destructive))' },
      { name: 'Unknown', value: unknown, color: 'hsl(220 9% 46%)' },
    ].filter((d) => d.value > 0);

    const flatChecks = Object.entries(allChecks).flatMap(([monitorId, checks]) =>
      checks.map((c) => ({ monitorId, ...c })),
    );
    const avgMs =
      monitors.length > 0
        ? Math.round(
            monitors
              .map((m) => m.lastResponseTime ?? 0)
              .filter((v) => v > 0)
              .reduce((a, b) => a + b, 0) /
              Math.max(1, monitors.filter((m) => m.lastResponseTime != null).length),
          )
        : 0;

    const responseTimeTrend = ['00:00', '02:00', '04:00', '06:00', '08:00', '10:00', '12:00', '14:00', '16:00', '18:00', '20:00', '22:00', 'Now'].map(
      (time, i) => ({
        time,
        ms: Math.max(0, avgMs + Math.round(Math.sin(i) * 30) + (i === 10 ? 50 : 0)),
      }),
    );

    const avgUptime =
      monitors.length > 0
        ? monitors.reduce((a, m) => a + m.uptimePercent, 0) / monitors.length
        : 100;

    const uptimeTrend = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].map((day, i) => ({
      day,
      uptime: Math.round((avgUptime - (i === 2 ? 0.5 : 0) + (i === 4 ? -0.2 : 0)) * 100) / 100,
    }));

    const uptime24h = ['00', '02', '04', '06', '08', '10', '12', '14', '16', '18', '20', '22', 'Now'].map((hour, i) => ({
      hour,
      uptime: Math.round((avgUptime - (offline > 0 && i >= 8 && i <= 12 ? 1.5 : 0)) * 100) / 100,
    }));

    const dayNames = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
    const incidentByDay: Record<string, number> = Object.fromEntries(dayNames.map((d) => [d, 0]));
    for (const inc of incidents) {
      const day = dayNames[new Date(inc.startedAt).getDay()];
      incidentByDay[day] = (incidentByDay[day] ?? 0) + 1;
    }
    const incidentTrend = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].map((day) => ({
      day,
      incidents: incidentByDay[day] ?? 0,
    }));

    void flatChecks;
    return {
      responseTimeTrend,
      uptimeTrend,
      uptime24h,
      incidentTrend,
      categoryDistribution,
      monitorDistribution: categoryDistribution,
      statusDistribution,
    };
  },

  getReportSummary: (from?: string, to?: string) => {
    const toDate = to ? new Date(`${to}T23:59:59`) : new Date();
    const fromDate = from
      ? new Date(`${from}T00:00:00`)
      : new Date(toDate.getTime() - 30 * 86_400_000);
    const fromMs = fromDate.getTime();
    const toMs = toDate.getTime();

    const inRange = (iso: string) => {
      const t = new Date(iso).getTime();
      return t >= fromMs && t <= toMs;
    };

    const monitors = get().monitors;
    const incidents = get().incidents.filter((i) => inRange(i.startedAt));
    const allChecks = get().monitorChecks;

    const checksInRange: { monitorId: string; status: string; responseTime: number | null; day: string }[] = [];
    for (const [monitorId, checks] of Object.entries(allChecks)) {
      for (const c of checks) {
        if (!inRange(c.createdAt)) continue;
        checksInRange.push({
          monitorId,
          status: c.status,
          responseTime: c.responseTime ?? null,
          day: c.createdAt.slice(0, 10),
        });
      }
    }

    const days: string[] = [];
    const cur = new Date(fromDate);
    cur.setHours(0, 0, 0, 0);
    const end = new Date(toDate);
    end.setHours(0, 0, 0, 0);
    while (cur <= end) {
      days.push(cur.toISOString().slice(0, 10));
      cur.setDate(cur.getDate() + 1);
    }

    const responseTrend = days.map((day) => {
      const dayChecks = checksInRange.filter((c) => c.day === day && c.responseTime != null);
      const ms =
        dayChecks.length > 0
          ? Math.round(dayChecks.reduce((sum, c) => sum + (c.responseTime ?? 0), 0) / dayChecks.length)
          : 0;
      return { day, ms };
    });

    const incidentTrend = days.map((day) => ({
      day,
      count: incidents.filter((i) => i.startedAt.slice(0, 10) === day).length,
    }));

    const monitorRows = monitors.map((m) => {
      const periodChecks = checksInRange.filter((c) => c.monitorId === m.id);
      const uptime =
        periodChecks.length > 0
          ? Math.round(
              (periodChecks.filter((c) => c.status === 'UP').length / periodChecks.length) * 10000,
            ) / 100
          : m.uptimePercent;
      return {
        name: m.name,
        uptime,
        status: m.status,
        category: m.category ?? null,
      };
    });

    const responseTimes = checksInRange
      .map((c) => c.responseTime)
      .filter((v): v is number => v != null);
    const avgResponseTime =
      responseTimes.length > 0
        ? Math.round(responseTimes.reduce((a, b) => a + b, 0) / responseTimes.length)
        : 0;

    const avgUptime =
      monitorRows.length > 0
        ? Math.round((monitorRows.reduce((a, m) => a + m.uptime, 0) / monitorRows.length) * 100) / 100
        : 100;

    const openIncidents = incidents.filter((i) => i.status !== 'RESOLVED').length;
    const resolvedIncidents = incidents.filter((i) => i.status === 'RESOLVED').length;

    return {
      summary: {
        totalMonitors: monitors.length,
        avgUptime,
        avgResponseTime,
        totalIncidents: incidents.length,
        openIncidents,
        resolvedIncidents,
      },
      responseTrend,
      incidentTrend,
      monitors: monitorRows,
      period: {
        from: fromDate.toISOString().slice(0, 10),
        to: toDate.toISOString().slice(0, 10),
      },
    };
  },
}));

export { delay as demoDelay };
