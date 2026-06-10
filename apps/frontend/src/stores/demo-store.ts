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
  MaintenanceWindow,
  TeamMember,
} from '@/lib/types';
import {
  initialMonitors,
  initialMonitorChecks,
  initialMonitorAlertHistory,
  initialIncidents,
  initialAlertChannels,
  initialAlertRules,
  initialRecentAlerts,
  initialTeamMembers,
  initialPendingInvitations,
  initialMaintenanceWindows,
  initialSettings,
  initialOrganization,
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
}

interface DemoState {
  monitors: Monitor[];
  monitorChecks: Record<string, MonitorCheck[]>;
  monitorAlertHistory: Record<string, { id: string; trigger: string; status: string; createdAt: string }[]>;
  incidents: Incident[];
  alertChannels: AlertChannel[];
  alertRules: AlertRule[];
  recentAlerts: typeof initialRecentAlerts;
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
  acknowledgeIncident: (id: string) => void;
  resolveIncident: (id: string) => void;

  addAlertChannel: (data: { channel: string; name: string; config: Record<string, unknown> }) => AlertChannel;
  addAlertRule: (data: { channelId: string; trigger: string; monitorId?: string }) => AlertRule;
  testAlertChannel: (channelId: string) => { status: string; message: string };

  inviteMember: (data: { email: string; role: string }) => void;
  updateMember: (id: string, data: Record<string, unknown>) => void;
  removeMember: (id: string) => void;

  createMaintenance: (data: Record<string, unknown>) => MaintenanceWindow;
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
  getReportSummary: (from?: string, to?: string) => {
    summary: {
      totalMonitors: number;
      avgUptime: number;
      avgResponseTime: number;
      totalIncidents: number;
      openIncidents: number;
    };
    responseTrend: { day: string; ms: number }[];
    incidentTrend: { day: string; count: number }[];
    monitors: { name: string; uptime: number; status: string }[];
  };
}

export const useDemoStore = create<DemoState>((set, get) => ({
  monitors: [...initialMonitors],
  monitorChecks: { ...initialMonitorChecks },
  monitorAlertHistory: { ...initialMonitorAlertHistory },
  incidents: [...initialIncidents],
  alertChannels: [...initialAlertChannels],
  alertRules: [...initialAlertRules],
  recentAlerts: [...initialRecentAlerts],
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

  acknowledgeIncident: (id) => {
    set((s) => ({
      incidents: s.incidents.map((i) =>
        i.id === id ? { ...i, status: 'ACKNOWLEDGED' } : i,
      ),
    }));
  },

  resolveIncident: (id) => {
    const now = new Date().toISOString();
    set((s) => ({
      incidents: s.incidents.map((i) =>
        i.id === id
          ? { ...i, status: 'RESOLVED', endedAt: now, duration: i.duration ?? 0 }
          : i,
      ),
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
    const window: MaintenanceWindow = {
      id: uid('mw'),
      title: String(data.title),
      monitor: monitor ? { id: monitor.id, name: monitor.name } : null,
      startTime: String(data.startTime),
      endTime: String(data.endTime),
      reason: data.reason ? String(data.reason) : null,
      pauseAlerts: Boolean(data.pauseAlerts),
      createdAt: new Date().toISOString(),
    };
    set((s) => ({ maintenanceWindows: [window, ...s.maintenanceWindows] }));
    return window;
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
    const checks: { id: string; monitor: string; status: string; responseTime: number | null; time: string }[] = [];
    for (const m of get().monitors.slice(0, 6)) {
      const latest = get().monitorChecks[m.id]?.[0];
      if (latest) {
        checks.push({
          id: latest.id,
          monitor: m.name,
          status: latest.status,
          responseTime: latest.responseTime ?? null,
          time: new Date(latest.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
        });
      }
    }
    return checks;
  },

  getReportSummary: () => {
    const monitors = get().monitors;
    const incidents = get().incidents;
    const avgUptime =
      monitors.length > 0
        ? Math.round((monitors.reduce((a, m) => a + m.uptimePercent, 0) / monitors.length) * 100) / 100
        : 100;
    const responseTimes = monitors
      .map((m) => m.lastResponseTime)
      .filter((v): v is number => v != null);
    const avgResponseTime =
      responseTimes.length > 0
        ? Math.round(responseTimes.reduce((a, b) => a + b, 0) / responseTimes.length)
        : 0;

    return {
      summary: {
        totalMonitors: monitors.length,
        avgUptime,
        avgResponseTime,
        totalIncidents: incidents.length,
        openIncidents: incidents.filter((i) => i.status !== 'RESOLVED').length,
      },
      responseTrend: [
        { day: '2026-06-03', ms: 128 },
        { day: '2026-06-04', ms: 142 },
        { day: '2026-06-05', ms: 156 },
        { day: '2026-06-06', ms: 135 },
        { day: '2026-06-07', ms: 148 },
        { day: '2026-06-08', ms: 131 },
        { day: '2026-06-09', ms: avgResponseTime },
      ],
      incidentTrend: [
        { day: '2026-06-03', count: 0 },
        { day: '2026-06-04', count: 1 },
        { day: '2026-06-05', count: 2 },
        { day: '2026-06-06', count: 0 },
        { day: '2026-06-07', count: 1 },
        { day: '2026-06-08', count: 0 },
        { day: '2026-06-09', count: incidents.filter((i) => i.status !== 'RESOLVED').length },
      ],
      monitors: monitors.map((m) => ({
        name: m.name,
        uptime: m.uptimePercent,
        status: m.status,
      })),
    };
  },
}));

export { delay as demoDelay };
