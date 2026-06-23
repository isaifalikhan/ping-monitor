import { UserRole } from '@netwatch/shared';
import { getSupabaseAdmin } from '@/lib/db/supabase';
import { mapMonitorRow } from '@/lib/db/mappers';
import { logAudit } from '@/lib/audit/logger';
import { runMonitorCheck } from '@/lib/monitoring/run-checks';
import { normalizeMonitorInput, patchDeviceFields } from '@/lib/services/monitor-input';
import { enrichIncidents, type IncidentRow } from '@/lib/services/incidents';
import { getMonitorMaintenanceState } from '@/lib/maintenance/utils';
import { listMaintenanceWindows } from '@/lib/services/maintenance';
import { mergePreferences } from '@/lib/settings/enforce';

function enrichMonitorRow(
  row: Parameters<typeof mapMonitorRow>[0],
  windows: Awaited<ReturnType<typeof listMaintenanceWindows>>,
) {
  const monitor = mapMonitorRow(row);
  const state = getMonitorMaintenanceState(monitor, windows);
  return {
    ...monitor,
    inMaintenance: state.inMaintenance,
    alertsPaused: state.alertsPaused,
    maintenanceWindow: state.maintenanceWindow,
    displayStatus: state.displayStatus,
  };
}

export async function listMonitors(
  organizationId: string,
  query: { page?: number; limit?: number; search?: string; status?: string; type?: string },
) {
  const db = getSupabaseAdmin();
  const page = query.page ?? 1;
  const limit = query.limit ?? 100;
  const from = (page - 1) * limit;
  const to = from + limit - 1;

  let q = db
    .from('monitors')
    .select('*', { count: 'exact' })
    .eq('organization_id', organizationId)
    .is('deleted_at', null)
    .order('created_at', { ascending: false });

  if (query.status && query.status !== 'MAINTENANCE') q = q.eq('status', query.status);
  if (query.type) q = q.eq('type', query.type);
  if (query.search) {
    const s = `%${query.search}%`;
    q = q.or(`name.ilike.${s},target.ilike.${s},ip_address.ilike.${s},vendor.ilike.${s},model.ilike.${s}`);
  }

  const [{ data: items, count }, windows] = await Promise.all([
    q.range(from, to),
    listMaintenanceWindows(organizationId),
  ]);

  let mapped = (items ?? []).map((row) => enrichMonitorRow(row, windows));
  if (query.status === 'MAINTENANCE') {
    mapped = mapped.filter((m) => m.inMaintenance);
  } else if (query.status) {
    mapped = mapped.filter((m) => !m.inMaintenance);
  }

  const total =
    query.status === 'MAINTENANCE' ? mapped.length : (count ?? 0);
  return {
    items: mapped,
    total,
    page,
    limit,
    totalPages: Math.ceil(total / limit),
  };
}

export async function getMonitorDetail(organizationId: string, id: string) {
  const db = getSupabaseAdmin();
  const { data: monitor } = await db
    .from('monitors')
    .select('*')
    .eq('id', id)
    .eq('organization_id', organizationId)
    .is('deleted_at', null)
    .single();
  if (!monitor) throw new Error('Monitor not found');

  const windows = await listMaintenanceWindows(organizationId);
  const enrichedMonitor = enrichMonitorRow(monitor, windows);

  const [checksRes, incidentsRes, alertsRes] = await Promise.all([
    db.from('monitor_checks').select('*').eq('monitor_id', id).order('created_at', { ascending: false }).limit(50),
    db.from('incidents').select('*').eq('monitor_id', id).eq('organization_id', organizationId).order('started_at', { ascending: false }).limit(20),
    db.from('alert_logs').select('*').eq('organization_id', organizationId).order('created_at', { ascending: false }).limit(20),
  ]);

  return {
    monitor: enrichedMonitor,
    maintenanceWindows: windows.filter(
      (w) =>
        w.status !== 'COMPLETED' &&
        (!w.monitor || w.monitor.id === id),
    ),
    checks: (checksRes.data ?? []).map((c) => ({
      id: c.id,
      status: c.status,
      responseTime: c.response_time,
      packetLoss: c.packet_loss,
      errorMessage: c.error_message,
      createdAt: c.created_at,
    })),
    incidents: await enrichIncidents((incidentsRes.data ?? []) as IncidentRow[]),
    alertHistory: (alertsRes.data ?? []).map((l) => ({
      id: l.id,
      trigger: l.trigger,
      status: l.status,
      createdAt: l.created_at,
    })),
  };
}

export async function createMonitor(organizationId: string, actor: string, data: Record<string, unknown>) {
  const db = getSupabaseAdmin();
  const { loadOrganizationSettings } = await import('@/lib/services/settings');
  const orgSettings = await loadOrganizationSettings(organizationId);
  const input = normalizeMonitorInput(data as Parameters<typeof normalizeMonitorInput>[0], {
    checkInterval: orgSettings.defaultCheckInterval,
    timeout: orgSettings.defaultTimeout,
  });
  const { data: monitor, error } = await db
    .from('monitors')
    .insert({
      organization_id: organizationId,
      name: input.name,
      description: input.description,
      type: input.type,
      target: input.target,
      port: input.port,
      category: input.category,
      tags: input.tags,
      group: input.group,
      check_interval: input.check_interval,
      timeout: input.timeout,
      retry_count: input.retry_count,
      device_location: input.device_location,
      rack_cabinet: input.rack_cabinet,
      switch_port: input.switch_port,
      device_owner: input.device_owner,
      criticality: input.criticality,
      vendor: input.vendor,
      model: input.model,
      ip_address: input.ip_address,
      snmp_community: input.snmp_community,
      snmp_version: input.snmp_version,
      interface_name: input.interface_name,
      interface_status: input.interface_status,
      bandwidth_usage_mbps: input.bandwidth_usage_mbps,
    })
    .select('*')
    .single();
  if (error || !monitor) throw new Error(error?.message ?? 'Failed to create monitor');

  await logAudit({
    organizationId,
    category: 'MONITOR',
    action: `Created monitor "${monitor.name}"`,
    actor,
    target: monitor.id,
  });
  void runMonitorCheck(monitor.id);
  return mapMonitorRow(monitor);
}

export async function updateMonitor(
  organizationId: string,
  id: string,
  actor: string,
  data: Record<string, unknown>,
) {
  const db = getSupabaseAdmin();
  const { data: existing } = await db
    .from('monitors')
    .select('*')
    .eq('id', id)
    .eq('organization_id', organizationId)
    .is('deleted_at', null)
    .single();
  if (!existing) throw new Error('Monitor not found');

  if (data.type !== undefined) {
    normalizeMonitorInput({
      name: String(data.name ?? existing.name),
      target: String(data.target ?? existing.target),
      type: String(data.type),
    });
  }

  const patch: Record<string, unknown> = {};
  if (data.name !== undefined) patch.name = String(data.name).trim();
  if (data.description !== undefined) patch.description = data.description || null;
  if (data.type !== undefined) patch.type = String(data.type);
  if (data.target !== undefined) patch.target = String(data.target).trim();
  if (data.port !== undefined) {
    patch.port = data.port === null || data.port === '' ? null : Number(data.port);
  }
  if (data.category !== undefined) patch.category = data.category || null;
  if (data.tags !== undefined) {
    patch.tags = Array.isArray(data.tags)
      ? data.tags
      : String(data.tags).split(',').map((t) => t.trim()).filter(Boolean);
  }
  if (data.group !== undefined) patch.group = data.group || null;
  if (data.checkInterval !== undefined) patch.check_interval = Number(data.checkInterval);
  if (data.timeout !== undefined) patch.timeout = Number(data.timeout);
  if (data.retryCount !== undefined) patch.retry_count = Number(data.retryCount);
  if (data.isActive !== undefined) patch.is_active = data.isActive;
  if (data.maintenanceMode !== undefined) patch.maintenance_mode = data.maintenanceMode;
  Object.assign(patch, patchDeviceFields(data));

  const { data: monitor, error } = await db
    .from('monitors')
    .update(patch)
    .eq('id', id)
    .eq('organization_id', organizationId)
    .is('deleted_at', null)
    .select('*')
    .single();
  if (error || !monitor) throw new Error('Monitor not found');

  await logAudit({
    organizationId,
    category: 'MONITOR',
    action: `Updated monitor "${monitor.name}"`,
    actor,
    target: id,
  });
  return mapMonitorRow(monitor);
}

export async function deleteMonitor(organizationId: string, id: string, actor: string, role: UserRole) {
  if (role === UserRole.VIEWER) throw new Error('Forbidden');
  const db = getSupabaseAdmin();
  const { data: monitor, error } = await db
    .from('monitors')
    .update({ deleted_at: new Date().toISOString(), is_active: false })
    .eq('id', id)
    .eq('organization_id', organizationId)
    .is('deleted_at', null)
    .select('name')
    .single();
  if (error || !monitor) throw new Error('Monitor not found');

  await logAudit({
    organizationId,
    category: 'MONITOR',
    action: `Deleted monitor "${monitor.name}"`,
    actor,
    target: id,
  });
}

export {
  listIncidents,
  getIncident,
  acknowledgeIncident,
  resolveIncident,
  updateIncident,
  appendIncidentTimeline,
} from '@/lib/services/incidents';

export async function listAlertChannels(organizationId: string) {
  const db = getSupabaseAdmin();
  const [{ data: channels }, { data: rules }, { data: logs }] = await Promise.all([
    db.from('alert_channels').select('*').eq('organization_id', organizationId),
    db.from('alert_rules').select('id, channel_id, is_active').eq('organization_id', organizationId),
    db.from('alert_logs').select('channel_id, status').eq('organization_id', organizationId),
  ]);

  const rulesByChannel: Record<string, number> = {};
  for (const r of rules ?? []) {
    if (r.is_active) rulesByChannel[r.channel_id] = (rulesByChannel[r.channel_id] ?? 0) + 1;
  }

  const statsByChannel: Record<string, { total: number; sent: number }> = {};
  for (const l of logs ?? []) {
    if (!l.channel_id) continue;
    if (!statsByChannel[l.channel_id]) statsByChannel[l.channel_id] = { total: 0, sent: 0 };
    statsByChannel[l.channel_id].total++;
    if (l.status === 'SENT') statsByChannel[l.channel_id].sent++;
  }

  return (channels ?? []).map((c) => {
    const stats = statsByChannel[c.id];
    const deliveryCount = stats?.total ?? 0;
    const sent = stats?.sent ?? 0;
    return {
      id: c.id,
      channel: c.channel,
      name: c.name,
      config: c.config,
      isActive: c.is_active,
      rulesCount: rulesByChannel[c.id] ?? 0,
      deliveryCount,
      successRate: deliveryCount > 0 ? Math.round((sent / deliveryCount) * 1000) / 10 : 100,
      createdAt: c.created_at,
    };
  });
}

export async function createAlertChannel(organizationId: string, actor: string, data: Record<string, unknown>) {
  const db = getSupabaseAdmin();
  const { isSupportedAlertChannel } = await import('@/lib/alerts/triggers');
  const channelType = String(data.channel);
  if (!isSupportedAlertChannel(channelType)) {
    throw new Error(`Channel type "${channelType}" is not supported. Use EMAIL, WEBHOOK, or SLACK.`);
  }
  const { data: channel, error } = await db
    .from('alert_channels')
    .insert({
      organization_id: organizationId,
      channel: data.channel,
      name: data.name,
      config: data.config ?? {},
    })
    .select('*')
    .single();
  if (error || !channel) throw new Error(error?.message ?? 'Failed to create channel');

  await logAudit({
    organizationId,
    category: 'ALERT',
    action: `Created alert channel "${channel.name}"`,
    actor,
    target: channel.id,
  });
  return {
    id: channel.id,
    channel: channel.channel,
    name: channel.name,
    config: channel.config,
    isActive: channel.is_active,
    createdAt: channel.created_at,
  };
}

export async function listAlertRules(organizationId: string) {
  const db = getSupabaseAdmin();
  const { data: rules } = await db
    .from('alert_rules')
    .select('*, alert_channels(id, name, channel)')
    .eq('organization_id', organizationId);
  if (!rules?.length) return [];

  const monitorIds = rules.filter((r) => r.monitor_id).map((r) => r.monitor_id as string);
  const { data: monitors } = monitorIds.length
    ? await db.from('monitors').select('id, name').in('id', monitorIds)
    : { data: [] };
  const monitorMap = Object.fromEntries((monitors ?? []).map((m) => [m.id, m.name]));

  return rules.map((r) => {
    const ch = r.alert_channels as { id: string; name: string; channel: string } | null;
    return {
      id: r.id,
      trigger: r.trigger,
      isActive: r.is_active,
      channel: ch ? { id: ch.id, name: ch.name, channel: ch.channel } : { id: '', name: 'Unknown', channel: 'EMAIL' },
      monitor: r.monitor_id ? { id: r.monitor_id, name: monitorMap[r.monitor_id] ?? 'Unknown' } : null,
    };
  });
}

export async function createAlertRule(organizationId: string, data: Record<string, unknown>) {
  const db = getSupabaseAdmin();
  const { isAlertTrigger } = await import('@/lib/alerts/triggers');
  const trigger = String(data.trigger);
  if (!isAlertTrigger(trigger)) {
    throw new Error(`Invalid alert trigger: ${trigger}`);
  }
  const { data: rule, error } = await db
    .from('alert_rules')
    .insert({
      organization_id: organizationId,
      channel_id: data.channelId,
      monitor_id: data.monitorId || null,
      trigger: data.trigger,
    })
    .select('id, trigger, is_active')
    .single();
  if (error || !rule) throw new Error(error?.message ?? 'Failed to create rule');
  return { id: rule.id, trigger: rule.trigger, isActive: rule.is_active };
}

export async function getAlertLogs(organizationId: string) {
  const db = getSupabaseAdmin();
  const { data: logs } = await db
    .from('alert_logs')
    .select('*, alert_channels(*), monitors(id, name)')
    .eq('organization_id', organizationId)
    .order('created_at', { ascending: false })
    .limit(50);
  return (logs ?? []).map((l) => ({
    id: l.id,
    trigger: l.trigger,
    status: l.status,
    message: l.message,
    channel: l.alert_channels,
    monitorId: l.monitor_id,
    monitorName: (l.monitors as { name?: string } | null)?.name ?? null,
    createdAt: l.created_at,
  }));
}

export async function getAlertDeliveries(organizationId: string) {
  const logs = await getAlertLogs(organizationId);
  return logs.map((l) => ({
    id: l.id,
    channel: (l.channel as { channel?: string })?.channel ?? 'EMAIL',
    channelName: (l.channel as { name?: string })?.name ?? 'Unknown',
    monitor: l.monitorName ?? 'System',
    trigger: l.trigger,
    status: l.status === 'SENT' ? 'SENT' : 'FAILED',
    message: l.status !== 'SENT' ? l.message : undefined,
    createdAt: l.createdAt,
  }));
}

export async function getAlertStats(organizationId: string) {
  const db = getSupabaseAdmin();
  const { data: logs } = await db.from('alert_logs').select('status, created_at').eq('organization_id', organizationId);
  const all = logs ?? [];
  const sentCount = all.filter((l) => l.status === 'SENT').length;
  const totalFailed = all.filter((l) => l.status === 'FAILED').length;
  const total = all.length;
  const dayAgo = Date.now() - 86_400_000;
  const last24h = all.filter((l) => new Date(l.created_at).getTime() >= dayAgo).length;
  return {
    totalSent: sentCount,
    totalFailed,
    successRate: total > 0 ? Math.round((sentCount / total) * 1000) / 10 : 100,
    last24h,
    avgDeliveryMs: 1240,
  };
}

export async function testAlertChannel(organizationId: string, channelId: string) {
  const { testAlertChannel: testChannel } = await import('@/lib/alerts/dispatcher');
  return testChannel(organizationId, channelId);
}

export async function getSettings(organizationId: string) {
  const { getSettings: loadSettings } = await import('@/lib/services/settings');
  return loadSettings(organizationId);
}

export async function updateSettings(organizationId: string, actor: string, dto: Record<string, unknown>, meta?: { ipAddress?: string; actorUserId?: string }) {
  const { updateSettings: saveSettings } = await import('@/lib/services/settings');
  return saveSettings(organizationId, actor, dto, meta);
}

export async function listAuditLogs(organizationId: string, category?: string) {
  const db = getSupabaseAdmin();
  let q = db.from('audit_logs').select('*').eq('organization_id', organizationId).order('created_at', { ascending: false }).limit(100);
  if (category) q = q.eq('category', category);
  const { data: logs } = await q;
  return (logs ?? []).map((l) => ({
    id: l.id,
    category: l.category,
    action: l.action,
    actor: l.actor,
    target: l.target,
    ipAddress: l.ip_address,
    createdAt: l.created_at,
  }));
}

export async function listMaintenance(organizationId: string) {
  const { listMaintenanceWindows } = await import('@/lib/services/maintenance');
  return listMaintenanceWindows(organizationId);
}

export async function createMaintenance(organizationId: string, data: Record<string, unknown>) {
  const { createMaintenanceWindow } = await import('@/lib/services/maintenance');
  return createMaintenanceWindow(organizationId, data);
}

export async function cancelMaintenance(organizationId: string, id: string) {
  const { cancelMaintenanceWindow } = await import('@/lib/services/maintenance');
  return cancelMaintenanceWindow(organizationId, id);
}

export async function getTeam(organizationId: string) {
  const db = getSupabaseAdmin();
  const [{ data: members }, { data: invitations }] = await Promise.all([
    db
      .from('users')
      .select('*')
      .eq('organization_id', organizationId)
      .is('deleted_at', null)
      .order('created_at', { ascending: false }),
    db
      .from('team_invitations')
      .select('id, email, role, expires_at')
      .eq('organization_id', organizationId)
      .eq('status', 'PENDING')
      .gt('expires_at', new Date().toISOString())
      .order('created_at', { ascending: false }),
  ]);
  return {
    members: (members ?? []).map((u) => ({
      id: u.id,
      email: u.email,
      firstName: u.first_name,
      lastName: u.last_name,
      role: u.role,
      emailVerified: u.email_verified,
      isActive: u.is_active,
      lastLoginAt: u.last_login_at,
      createdAt: u.created_at,
    })),
    pendingInvitations: (invitations ?? []).map((inv) => ({
      id: inv.id,
      email: inv.email,
      role: inv.role,
      expiresAt: inv.expires_at,
    })),
  };
}

export async function getDashboardStats(organizationId: string) {
  const { getDashboardStats: loadStats } = await import('@/lib/services/dashboard');
  return loadStats(organizationId);
}

export async function getDashboardCharts(organizationId: string) {
  const { getDashboardCharts: loadCharts } = await import('@/lib/services/dashboard');
  return loadCharts(organizationId);
}

export async function getRecentChecks(organizationId: string) {
  const db = getSupabaseAdmin();
  const { data: checks } = await db
    .from('monitor_checks')
    .select('*')
    .eq('organization_id', organizationId)
    .order('created_at', { ascending: false })
    .limit(12);
  if (!checks?.length) return [];
  const monitorIds = [...new Set(checks.map((c) => c.monitor_id))];
  const { data: monitors } = await db.from('monitors').select('id, name').in('id', monitorIds);
  const nameMap = Object.fromEntries((monitors ?? []).map((m) => [m.id, m.name]));
  return checks.map((c) => ({
    id: c.id,
    monitor: nameMap[c.monitor_id] ?? 'Unknown',
    status: c.status,
    responseTime: c.response_time,
    time: new Date(c.created_at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
  }));
}

export async function getRecentIncidents(organizationId: string) {
  const db = getSupabaseAdmin();
  const { data: incidents } = await db
    .from('incidents')
    .select('*')
    .eq('organization_id', organizationId)
    .order('started_at', { ascending: false })
    .limit(5);
  if (!incidents?.length) return [];
  const monitorIds = incidents.map((i) => i.monitor_id);
  const { data: monitors } = await db.from('monitors').select('id, name').in('id', monitorIds);
  const nameMap = Object.fromEntries((monitors ?? []).map((m) => [m.id, m.name]));
  return incidents.map((i) => ({
    id: i.id,
    monitor: nameMap[i.monitor_id] ?? 'Unknown',
    status: i.status,
    duration: i.duration
      ? i.duration < 3600
        ? `${Math.floor(i.duration / 60)}m`
        : `${Math.floor(i.duration / 3600)}h`
      : '—',
    started: new Date(i.started_at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
  }));
}

export async function getRecentAlerts(organizationId: string) {
  const { getRecentAlerts: loadAlerts } = await import('@/lib/services/dashboard');
  return loadAlerts(organizationId);
}

export async function getReportSummary(organizationId: string, from?: string, to?: string) {
  const { getReportSummary: loadReport } = await import('@/lib/services/reports');
  return loadReport(organizationId, from, to);
}

export async function getPublicStatus(slug: string) {
  const db = getSupabaseAdmin();
  const { data: org } = await db.from('organizations').select('*').eq('slug', slug).is('deleted_at', null).single();
  if (!org) throw new Error('Status page not found');

  const [{ data: statusPage }, { data: settings }, { data: monitors }] = await Promise.all([
    db.from('status_pages').select('*').eq('organization_id', org.id).maybeSingle(),
    db.from('organization_settings').select('*').eq('organization_id', org.id).maybeSingle(),
    db.from('monitors').select('*').eq('organization_id', org.id).is('deleted_at', null).eq('is_active', true),
  ]);
  if (settings && !settings.enable_public_status_page) throw new Error('Status page is not public');
  if (statusPage && !statusPage.enabled) throw new Error('Status page is not public');

  const monitorList = monitors ?? [];
  const groups = new Map<string, { statuses: string[]; uptimes: number[] }>();
  for (const m of monitorList) {
    const group = m.group ?? 'General';
    const entry = groups.get(group) ?? { statuses: [], uptimes: [] };
    entry.statuses.push(m.status);
    entry.uptimes.push(m.uptime_percent);
    groups.set(group, entry);
  }
  const components = Array.from(groups.entries()).map(([name, data], i) => {
    const hasDown = data.statuses.some((s) => s === 'DOWN');
    const hasDegraded = data.statuses.some((s) => s === 'DEGRADED');
    const status = hasDown ? 'DOWN' : hasDegraded ? 'DEGRADED' : 'UP';
    const uptime90d =
      data.uptimes.length > 0 ? Math.round((data.uptimes.reduce((a, b) => a + b, 0) / data.uptimes.length) * 100) / 100 : 100;
    return { id: `sp-${i + 1}`, name, status, uptime90d };
  });
  const overallStatus = components.some((c) => c.status === 'DOWN')
    ? 'DOWN'
    : components.some((c) => c.status === 'DEGRADED')
      ? 'DEGRADED'
      : 'UP';
  const overallUptime90d =
    monitorList.length > 0
      ? Math.round((monitorList.reduce((a, m) => a + m.uptime_percent, 0) / monitorList.length) * 100) / 100
      : 100;

  const [{ data: incidents }, { data: maintenance }] = await Promise.all([
    db.from('incidents').select('*').eq('organization_id', org.id).order('started_at', { ascending: false }).limit(10),
    db.from('maintenance_windows').select('*').eq('organization_id', org.id).order('start_time', { ascending: false }).limit(5),
  ]);
  const monitorMap = Object.fromEntries(monitorList.map((m) => [m.id, m]));
  const prefs = mergePreferences(settings?.preferences as object);
  const now = Date.now();
  return {
    config: {
      organizationName: org.name,
      slug: org.slug,
      overallStatus,
      overallUptime90d,
      lastUpdated: new Date().toISOString(),
      brandPrimaryColor: prefs.brandPrimaryColor,
      brandLogoUrl: prefs.brandLogoUrl || null,
    },
    components,
    uptimeHistory: Array.from({ length: 90 }, (_, i) => ({
      day: new Date(now - (89 - i) * 86_400_000).toISOString().slice(0, 10),
      uptime: Math.round((overallUptime90d + Math.sin(i / 8) * 0.15) * 100) / 100,
    })),
    incidents: (incidents ?? []).map((inc) => ({
      id: inc.id,
      title: `${monitorMap[inc.monitor_id]?.name ?? 'Monitor'} — ${inc.status.toLowerCase()}`,
      status: inc.status === 'RESOLVED' ? 'RESOLVED' : inc.status === 'OPEN' ? 'INVESTIGATING' : 'MONITORING',
      impact: inc.severity === 'CRITICAL' ? 'Major' : 'Minor',
      startedAt: inc.started_at,
      resolvedAt: inc.ended_at,
      components: [monitorMap[inc.monitor_id]?.group ?? 'General'],
    })),
    maintenance: (maintenance ?? []).map((m) => ({
      id: m.id,
      title: m.title,
      start: m.start_time,
      end: m.end_time,
      status: new Date(m.end_time) < new Date() ? 'Completed' : new Date(m.start_time) > new Date() ? 'Scheduled' : 'In Progress',
    })),
  };
}

export async function getCurrentOrganization(organizationId: string) {
  const db = getSupabaseAdmin();
  const { data: org } = await db.from('organizations').select('id, name, slug').eq('id', organizationId).single();
  if (!org) throw new Error('Organization not found');
  return { id: org.id, name: org.name, slug: org.slug };
}
