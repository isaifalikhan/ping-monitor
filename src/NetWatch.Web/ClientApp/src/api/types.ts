// Types mirroring the server DTOs (NetWatch.Web.Api.Dtos).

export type DeviceType = 'Switch' | 'Router' | 'Firewall' | 'Server' | 'VM' | 'DomainController' | 'Hypervisor' | 'VCenter' | 'Other';
export const DEVICE_TYPES: DeviceType[] = ['Switch', 'Router', 'Firewall', 'Server', 'VM', 'DomainController', 'Hypervisor', 'VCenter', 'Other'];
export type DeviceStatus = 'Unknown' | 'Up' | 'Warning' | 'Down' | 'Maintenance';
export type OsKind = 'Unknown' | 'Windows' | 'Linux' | 'NetworkOs' | 'Esxi';
export const OS_KINDS: OsKind[] = ['Unknown', 'Windows', 'Linux', 'NetworkOs', 'Esxi'];
export type Severity = 'Info' | 'Warning' | 'Major' | 'Critical';
export const SEVERITIES: Severity[] = ['Info', 'Warning', 'Major', 'Critical'];
export type AlertState = 'Active' | 'Acknowledged' | 'Resolved' | 'Closed';
export type Role = 'Admin' | 'Operator' | 'Viewer';
export type CredentialKind = 'SnmpV2c' | 'SnmpV3' | 'Windows' | 'Ssh' | 'VCenter';
export type GroupKind = 'Site' | 'Building' | 'Floor' | 'Rack' | 'Logical';
export const GROUP_KINDS: GroupKind[] = ['Site', 'Building', 'Floor', 'Rack', 'Logical'];
export type RuleType = 'Threshold' | 'StateChange' | 'EventMatch';
export type Operator = 'GreaterThan' | 'GreaterOrEqual' | 'LessThan' | 'LessOrEqual' | 'Equal' | 'NotEqual';
export type SnmpAuth = 'None' | 'MD5' | 'SHA1' | 'SHA256' | 'SHA384' | 'SHA512';
export type SnmpPriv = 'None' | 'DES' | 'AES128' | 'AES192' | 'AES256';

export const severityRank: Record<Severity, number> = { Info: 0, Warning: 1, Major: 2, Critical: 3 };

export interface CurrentUser {
  id: number;
  username: string;
  displayName: string;
  role: Role;
  mustChangePassword: boolean;
  sessionIdleTimeoutMin: number;
}

export interface DeviceListItem {
  id: number;
  name: string;
  ipAddress: string;
  type: DeviceType;
  vendor?: string | null;
  model?: string | null;
  groupId?: number | null;
  groupPath?: string | null;
  status: DeviceStatus;
  rawStatus: DeviceStatus;
  worstAlert?: Severity | null;
  maintenanceMode: boolean;
  isEnabled: boolean;
  lastPollUtc?: string | null;
  statusSinceUtc: string;
  rttMs?: number | null;
  lossPct?: number | null;
  cpuPct?: number | null;
  memPct?: number | null;
  tags: string[];
}

export interface DeviceDetail extends Omit<DeviceListItem, 'tags'> {
  hostname?: string | null;
  os: OsKind;
  serialNumber?: string | null;
  firmware?: string | null;
  sysObjectId?: string | null;
  sysName?: string | null;
  sysDescription?: string | null;
  location?: string | null;
  pollIntervalSec: number;
  maintenanceUntilUtc?: string | null;
  parentDeviceId?: number | null;
  parentDeviceName?: string | null;
  uptimeSec?: number | null;
  tempC?: number | null;
  notes?: string | null;
  tags: string[];
  credentials: { purpose: string; credentialProfileId: number; profileName?: string | null }[];
  createdUtc: string;
  updatedUtc: string;
}

export interface DeviceInput {
  name: string;
  ipAddress: string;
  hostname?: string | null;
  type: DeviceType;
  os: OsKind;
  location?: string | null;
  groupId?: number | null;
  pollIntervalSec?: number | null;
  isEnabled: boolean;
  parentDeviceId?: number | null;
  snmpCredentialId?: number | null;
  notes?: string | null;
  tags: string[];
}

export interface NetInterface {
  id: number;
  deviceId: number;
  ifIndex: number;
  name: string;
  description?: string | null;
  alias?: string | null;
  ifType: number;
  speedBps: number;
  mac?: string | null;
  adminStatus: number;
  operStatus: number;
  lastChangeUtc?: string | null;
  isImportant: boolean;
  isMonitored: boolean;
  inBps?: number | null;
  outBps?: number | null;
  inUtilPct?: number | null;
  outUtilPct?: number | null;
  errorsPs?: number | null;
}

export interface Sensor {
  id: number;
  kind: 'Temperature' | 'Fan' | 'PowerSupply' | 'Other';
  name: string;
  lastValue?: number | null;
  unit?: string | null;
  lastState: 'Unknown' | 'Ok' | 'Warning' | 'Critical' | 'NotPresent';
  lastSeenUtc: string;
}

export interface DeviceEvent {
  id: number;
  timestampUtc: string;
  deviceId?: number | null;
  deviceName?: string | null;
  kind: string;
  severity: Severity;
  message: string;
}

export interface Series {
  resolution: 'Raw' | 'FiveMinute' | 'Hourly';
  ts: number[];
  avg: Record<string, (number | null)[]>;
  max?: Record<string, (number | null)[]> | null;
}

export interface Group {
  id: number;
  parentId?: number | null;
  name: string;
  kind: GroupKind;
  sortOrder: number;
  deviceCount: number;
}

export interface Credential {
  id: number;
  name: string;
  kind: CredentialKind;
  displayHint: string;
  usedBy: number;
  updatedUtc: string;
}

export interface Alert {
  id: number;
  ruleId?: number | null;
  deviceId?: number | null;
  deviceName?: string | null;
  interfaceId?: number | null;
  severity: Severity;
  state: AlertState;
  title: string;
  message: string;
  currentValue?: number | null;
  autoResolve: boolean;
  firstSeenUtc: string;
  lastSeenUtc: string;
  occurrenceCount: number;
  ackByUsername?: string | null;
  ackUtc?: string | null;
  ackComment?: string | null;
  resolvedUtc?: string | null;
  closedUtc?: string | null;
  closedByUsername?: string | null;
}

export interface AlertHistory {
  timestampUtc: string;
  action: string;
  username?: string | null;
  comment?: string | null;
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface RuleScope {
  deviceIds: number[];
  groupIds: number[];
  deviceTypes: DeviceType[];
  tags: string[];
  importantInterfacesOnly: boolean;
}

export interface Rule {
  id: number;
  name: string;
  description?: string | null;
  isEnabled: boolean;
  isBuiltIn: boolean;
  ruleType: RuleType;
  severity: Severity;
  scope: RuleScope;
  key: string;
  operator?: Operator | null;
  threshold?: number | null;
  clearThreshold?: number | null;
  durationSec: number;
  updatedUtc: string;
}

export interface RuleKey {
  ruleType: RuleType;
  key: string;
  displayName: string;
}

export interface User {
  id: number;
  username: string;
  displayName: string;
  email?: string | null;
  role: Role;
  authSource: 'Local' | 'Ldap';
  isEnabled: boolean;
  isLocked: boolean;
  lastLoginUtc?: string | null;
  mustChangePassword: boolean;
  createdUtc: string;
}

export interface AuditEntry {
  id: number;
  timestampUtc: string;
  username: string;
  action: string;
  targetType?: string | null;
  targetId?: string | null;
  details?: string | null;
  clientIp?: string | null;
}

export interface StatusCounts {
  total: number;
  up: number;
  warning: number;
  down: number;
  unknown: number;
  maintenance: number;
}

export interface Overview {
  totals: StatusCounts;
  byType: { type: DeviceType; counts: StatusCounts }[];
  alerts: Record<Severity, number>;
  groups: { groupId: number; name: string; path: string; counts: StatusCounts }[];
  topInterfaces: {
    interfaceId: number;
    deviceId: number;
    deviceName: string;
    interfaceName: string;
    speedBps: number;
    inBps?: number | null;
    outBps?: number | null;
    inUtilPct?: number | null;
    outUtilPct?: number | null;
  }[];
  topCpu: { deviceId: number; name: string; type: DeviceType; value: number }[];
  topMemory: { deviceId: number; name: string; type: DeviceType; value: number }[];
  problemDevices: DeviceListItem[];
  recentEvents: DeviceEvent[];
}

export interface MonitoringSettings {
  defaultPollIntervalSec: number;
  downAfterFailedPolls: number;
  pingCount: number;
  pingTimeoutMs: number;
  snmpTimeoutMs: number;
  snmpRetries: number;
  maxConcurrentPolls: number;
  interfaceMetadataRefreshMin: number;
  inventoryRefreshHours: number;
}

export interface SecuritySettings {
  sessionIdleTimeoutMin: number;
  sessionAbsoluteTimeoutHours: number;
  maxFailedLogins: number;
  lockoutMinutes: number;
  minPasswordLength: number;
}

export interface RetentionSettings {
  rawDays: number;
  fiveMinuteDays: number;
  hourlyDays: number;
  eventDays: number;
  auditDays: number;
  closedAlertDays: number;
}

export interface DiscoveryJob {
  id: string;
  cidr: string;
  state: 'Running' | 'Completed' | 'Failed' | 'Cancelled';
  total: number;
  scanned: number;
  error?: string | null;
  startedUtc: string;
  results: DiscoveryResult[];
}

export interface DiscoveryResult {
  ipAddress: string;
  rttMs?: number | null;
  respondsToPing: boolean;
  snmpCredentialId?: number | null;
  sysName?: string | null;
  sysDescription?: string | null;
  sysObjectId?: string | null;
  vendor?: string | null;
  model?: string | null;
  suggestedType: DeviceType;
  alreadyMonitored: boolean;
}
