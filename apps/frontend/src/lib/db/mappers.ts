export type MonitorRow = {
  id: string;
  organization_id: string;
  name: string;
  description: string | null;
  type: string;
  target: string;
  status: string;
  tags: string[];
  group: string | null;
  category: string | null;
  port: number | null;
  device_location: string | null;
  rack_cabinet: string | null;
  switch_port: string | null;
  device_owner: string | null;
  criticality: string | null;
  vendor: string | null;
  model: string | null;
  ip_address: string | null;
  snmp_community: string | null;
  snmp_version: string | null;
  interface_name: string | null;
  interface_status: string | null;
  bandwidth_usage_mbps: number | null;
  check_interval: number;
  timeout: number;
  retry_count: number;
  is_active: boolean;
  maintenance_mode: boolean;
  last_response_time: number | null;
  last_checked_at: string | null;
  uptime_percent: number;
  consecutive_failures: number;
  created_at: string;
  updated_at: string;
};

export function mapMonitorRow(m: MonitorRow) {
  return {
    id: m.id,
    name: m.name,
    description: m.description,
    type: m.type,
    target: m.target,
    status: m.status,
    tags: m.tags ?? [],
    group: m.group,
    category: m.category,
    port: m.port,
    deviceLocation: m.device_location,
    rackCabinet: m.rack_cabinet,
    switchPort: m.switch_port,
    deviceOwner: m.device_owner,
    criticality: m.criticality ?? 'MEDIUM',
    vendor: m.vendor,
    model: m.model,
    ipAddress: m.ip_address,
    snmpCommunity: m.snmp_community,
    snmpVersion: m.snmp_version,
    interfaceName: m.interface_name,
    interfaceStatus: m.interface_status,
    bandwidthUsageMbps: m.bandwidth_usage_mbps,
    checkInterval: m.check_interval,
    timeout: m.timeout,
    retryCount: m.retry_count,
    isActive: m.is_active,
    maintenanceMode: m.maintenance_mode,
    lastResponseTime: m.last_response_time,
    lastCheckedAt: m.last_checked_at,
    uptimePercent: m.uptime_percent,
    createdAt: m.created_at,
    updatedAt: m.updated_at,
  };
}

export type UserRow = {
  id: string;
  email: string;
  first_name: string;
  last_name: string;
  role: string;
  email_verified: boolean;
  organization_id: string;
};

export function mapUserRow(u: UserRow) {
  return {
    id: u.id,
    email: u.email,
    firstName: u.first_name,
    lastName: u.last_name,
    role: u.role,
    organizationId: u.organization_id,
    emailVerified: u.email_verified,
  };
}
