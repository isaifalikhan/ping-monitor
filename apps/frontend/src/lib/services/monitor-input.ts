import { isSupportedMonitorType } from '@/lib/validations/monitor';

function trimOrNull(value: unknown): string | null {
  if (value === undefined || value === null || value === '') return null;
  const s = String(value).trim();
  return s || null;
}

export function normalizeMonitorInput(
  data: {
    name: string;
    description?: string | null;
    type: string;
    target: string;
    port?: number | null;
    category?: string | null;
    checkInterval?: number;
    timeout?: number;
    retryCount?: number;
    group?: string | null;
    tags?: string[] | string;
    deviceLocation?: string | null;
    rackCabinet?: string | null;
    switchPort?: string | null;
    deviceOwner?: string | null;
    criticality?: string | null;
    vendor?: string | null;
    model?: string | null;
    ipAddress?: string | null;
    snmpCommunity?: string | null;
    snmpVersion?: string | null;
    interfaceName?: string | null;
    interfaceStatus?: string | null;
    bandwidthUsageMbps?: number | null;
  },
  defaults?: { checkInterval?: number; timeout?: number },
) {
  if (!isSupportedMonitorType(data.type)) {
    throw new Error(`Monitor type "${data.type}" is not supported`);
  }

  const tags = Array.isArray(data.tags)
    ? data.tags
    : typeof data.tags === 'string'
      ? data.tags.split(',').map((t) => t.trim()).filter(Boolean)
      : [];

  const port =
    data.port === undefined || data.port === null || Number.isNaN(data.port as number)
      ? null
      : Number(data.port);

  const bandwidth =
    data.bandwidthUsageMbps === undefined ||
    data.bandwidthUsageMbps === null ||
    Number.isNaN(data.bandwidthUsageMbps as number)
      ? null
      : Number(data.bandwidthUsageMbps);

  return {
    name: data.name.trim(),
    description: data.description?.trim() || null,
    type: data.type,
    target: data.target.trim(),
    port,
    category: data.category || null,
    tags,
    group: data.group?.trim() || null,
    check_interval: Number(data.checkInterval ?? defaults?.checkInterval ?? 60),
    timeout: Number(data.timeout ?? defaults?.timeout ?? 30),
    retry_count: Number(data.retryCount ?? 3),
    device_location: trimOrNull(data.deviceLocation),
    rack_cabinet: trimOrNull(data.rackCabinet),
    switch_port: trimOrNull(data.switchPort),
    device_owner: trimOrNull(data.deviceOwner),
    criticality: data.criticality || 'MEDIUM',
    vendor: trimOrNull(data.vendor),
    model: trimOrNull(data.model),
    ip_address: trimOrNull(data.ipAddress),
    snmp_community: trimOrNull(data.snmpCommunity),
    snmp_version: trimOrNull(data.snmpVersion),
    interface_name: trimOrNull(data.interfaceName),
    interface_status: trimOrNull(data.interfaceStatus),
    bandwidth_usage_mbps: bandwidth,
  };
}

/** Map camelCase API patch keys to normalized DB column values. */
export function patchDeviceFields(data: Record<string, unknown>): Record<string, unknown> {
  const patch: Record<string, unknown> = {};
  const map: [string, string][] = [
    ['deviceLocation', 'device_location'],
    ['rackCabinet', 'rack_cabinet'],
    ['switchPort', 'switch_port'],
    ['deviceOwner', 'device_owner'],
    ['criticality', 'criticality'],
    ['vendor', 'vendor'],
    ['model', 'model'],
    ['ipAddress', 'ip_address'],
    ['snmpCommunity', 'snmp_community'],
    ['snmpVersion', 'snmp_version'],
    ['interfaceName', 'interface_name'],
    ['interfaceStatus', 'interface_status'],
    ['bandwidthUsageMbps', 'bandwidth_usage_mbps'],
  ];
  for (const [apiKey, dbKey] of map) {
    if (data[apiKey] !== undefined) {
      patch[dbKey] =
        apiKey === 'bandwidthUsageMbps'
          ? data[apiKey] === null || data[apiKey] === ''
            ? null
            : Number(data[apiKey])
          : trimOrNull(data[apiKey]);
    }
  }
  return patch;
}
