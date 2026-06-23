import { z } from 'zod';
import {
  SUPPORTED_MONITOR_TYPES,
  MONITOR_CATEGORIES,
  CRITICALITY_LEVELS,
  SNMP_VERSIONS,
} from '@/lib/validations/monitor';

const portSchema = z.union([
  z.coerce.number().int().min(1).max(65535),
  z.null(),
  z.undefined(),
]);

const optionalText = (max: number) =>
  z.union([z.string().max(max), z.null(), z.undefined()]);

const deviceFieldsApiSchema = {
  deviceLocation: optionalText(200),
  rackCabinet: optionalText(100),
  switchPort: optionalText(50),
  deviceOwner: optionalText(100),
  criticality: z.enum(CRITICALITY_LEVELS).optional(),
  vendor: optionalText(100),
  model: optionalText(100),
  ipAddress: optionalText(45),
  snmpCommunity: optionalText(200),
  snmpVersion: z.enum(SNMP_VERSIONS).optional().nullable(),
  interfaceName: optionalText(100),
  interfaceStatus: optionalText(50),
  bandwidthUsageMbps: z.coerce.number().min(0).optional().nullable(),
};

export const createMonitorApiSchema = z.object({
  name: z.string().min(1).max(100),
  description: z.string().max(500).optional().nullable(),
  type: z.enum(SUPPORTED_MONITOR_TYPES),
  target: z.string().min(1).max(500),
  port: portSchema.optional(),
  category: z.enum(MONITOR_CATEGORIES).optional().nullable(),
  checkInterval: z.coerce.number().min(30).max(86400).default(60),
  timeout: z.coerce.number().min(5).max(300).default(30),
  retryCount: z.coerce.number().min(0).max(10).default(3),
  group: z.string().max(100).optional().nullable(),
  tags: z.array(z.string()).optional(),
  ...deviceFieldsApiSchema,
});

export const updateMonitorApiSchema = createMonitorApiSchema.partial().extend({
  isActive: z.boolean().optional(),
  maintenanceMode: z.boolean().optional(),
});
