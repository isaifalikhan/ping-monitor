import { z } from 'zod';

/** Monitor check types supported in production */
export const SUPPORTED_MONITOR_TYPES = [
  'HTTP',
  'HTTPS',
  'API',
  'TCP',
  'DNS',
  'SSL',
  'PING',
] as const;

export type SupportedMonitorType = (typeof SUPPORTED_MONITOR_TYPES)[number];

export const MONITOR_TYPES = SUPPORTED_MONITOR_TYPES;

export const MONITOR_TYPE_LABELS: Record<SupportedMonitorType, string> = {
  HTTP: 'HTTP',
  HTTPS: 'HTTPS',
  API: 'API',
  TCP: 'TCP',
  DNS: 'DNS',
  SSL: 'SSL',
  PING: 'PING (TCP)',
};

export const MONITOR_CATEGORIES = [
  'ERP',
  'Server',
  'Switch',
  'Router',
  'Firewall',
  'Database',
  'Website',
  'API',
  'DNS',
  'SSL',
  'Other',
] as const;

export type MonitorCategory = (typeof MONITOR_CATEGORIES)[number];

export const NETWORK_DEVICE_CATEGORIES = ['Switch', 'Router', 'Firewall'] as const;

export const CRITICALITY_LEVELS = ['LOW', 'MEDIUM', 'HIGH', 'CRITICAL'] as const;

export type CriticalityLevel = (typeof CRITICALITY_LEVELS)[number];

export const SNMP_VERSIONS = ['v1', 'v2c', 'v3'] as const;

const optionalText = (max: number) =>
  z.preprocess(
    (v) => (v === '' || v === null || v === undefined ? undefined : String(v).trim()),
    z.string().max(max).optional(),
  );

export const deviceFieldsSchema = z.object({
  deviceLocation: optionalText(200),
  rackCabinet: optionalText(100),
  switchPort: optionalText(50),
  deviceOwner: optionalText(100),
  criticality: z.enum(CRITICALITY_LEVELS).default('MEDIUM'),
  vendor: optionalText(100),
  model: optionalText(100),
  ipAddress: optionalText(45),
  snmpCommunity: optionalText(200),
  snmpVersion: z.preprocess(
    (v) => (v === '' || v === null || v === undefined ? undefined : v),
    z.enum(SNMP_VERSIONS).optional(),
  ),
  interfaceName: optionalText(100),
  interfaceStatus: optionalText(50),
  bandwidthUsageMbps: z.preprocess(
    (v) => (v === '' || v === null || v === undefined ? undefined : Number(v)),
    z.number().min(0).optional(),
  ),
});

export const createMonitorSchema = z.object({
  name: z.string().min(1, 'Name is required').max(100),
  description: z.string().max(500).optional(),
  type: z.enum(SUPPORTED_MONITOR_TYPES),
  target: z.string().min(1, 'Target is required').max(500),
  port: z.preprocess(
    (v) => (v === '' || v === null || v === undefined ? undefined : Number(v)),
    z.number().int().min(1).max(65535).optional(),
  ),
  category: z.enum(MONITOR_CATEGORIES).optional(),
  checkInterval: z.coerce.number().min(30).max(86400).default(60),
  timeout: z.coerce.number().min(5).max(300).default(30),
  retryCount: z.coerce.number().min(0).max(10).default(3),
  group: z.string().max(100).optional(),
  tags: z.string().optional(),
}).merge(deviceFieldsSchema);

export type CreateMonitorFormData = z.infer<typeof createMonitorSchema>;

export function isSupportedMonitorType(type: string): type is SupportedMonitorType {
  return (SUPPORTED_MONITOR_TYPES as readonly string[]).includes(type);
}

export function isNetworkDeviceCategory(category: string | null | undefined): boolean {
  return NETWORK_DEVICE_CATEGORIES.includes(category as (typeof NETWORK_DEVICE_CATEGORIES)[number]);
}
