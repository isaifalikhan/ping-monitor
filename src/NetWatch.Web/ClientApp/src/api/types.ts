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
export type RuleType = 'Threshold' | 'StateChange' | 'EventMatch' | 'CountInWindow' | 'Absence';
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
  os: OsKind;
  maintenanceReason?: string | null;
  /** Name of the unreachable upstream device when this device is down/warning because of it. */
  unreachableVia?: string | null;
}

export interface DeviceDetail extends Omit<DeviceListItem, 'tags' | 'os'> {
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
  watchedServices: string[];
  sshHostKeyFingerprint?: string | null;
  tlsThumbprint?: string | null;
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
  managementCredentialId?: number | null;
  watchedServices?: string[];
  vcenterCredentialId?: number | null;
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
  /** dot3StatsDuplexStatus: 1 unknown, 2 half, 3 full. */
  duplex?: number | null;
  expectedSpeedBps?: number | null;
  flapCount24h?: number;
}

export interface TopologyGraphNode {
  id: number;
  name: string;
  ipAddress: string;
  type: DeviceType;
  status: DeviceStatus;
  worstAlert?: Severity | null;
  parentDeviceId?: number | null;
}

export interface TopologyLink {
  id: number;
  deviceId: number;
  localPort: string;
  localInterfaceId?: number | null;
  remoteDeviceId?: number | null;
  remoteName?: string | null;
  remotePort?: string | null;
  protocol: string;
  operStatus?: number | null;
  utilPct?: number | null;
  speedBps?: number | null;
  lastSeenUtc: string;
}

export interface TopologyGraph {
  nodes: TopologyGraphNode[];
  links: TopologyLink[];
  unmatched: string[];
}

export interface DependencySuggestion {
  deviceId: number;
  deviceName: string;
  currentParentId?: number | null;
  currentParentName?: string | null;
  suggestedParentId: number;
  suggestedParentName: string;
  via: string;
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
  isFlapping: boolean;
  flapCount: number;
  escalatedUtc?: string | null;
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
  filter: EventFilter;
  windowSec: number;
  countThreshold: number;
  escalateAfterMin?: number | null;
  groupByField?: string | null;
}

export interface EventFilter {
  pattern?: string | null;
  minSeverity?: Severity | null;
  eventIds: number[];
  source?: string | null;
  oid?: string | null;
  fieldEquals?: Record<string, string>;
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
  topDisks: { deviceId: number; deviceName: string; volume: string; usedPct: number; freeBytes: number; totalBytes: number }[];
  received: { syslogLastHour: number; syslogErrorsLastHour: number; trapsLastHour: number };
  inMaintenance: number;
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

// ---------------------------------------------------------------- Phase 2

export type ServiceState = 'Unknown' | 'Running' | 'Stopped' | 'StartPending' | 'StopPending' | 'Paused' | 'Failed' | 'NotFound';
export type CollectionMethod = 'None' | 'WinRm' | 'Dcom' | 'Ssh' | 'Snmp';

export interface ServerListItem {
  id: number;
  name: string;
  ipAddress: string;
  type: DeviceType;
  os: OsKind;
  status: DeviceStatus;
  worstAlert?: Severity | null;
  groupPath?: string | null;
  cpuPct?: number | null;
  memPct?: number | null;
  worstDiskPct?: number | null;
  worstDisk?: string | null;
  servicesTotal: number;
  servicesRunning: number;
  method?: CollectionMethod | null;
  osName?: string | null;
  lastBootUtc?: string | null;
  pendingReboot?: boolean | null;
  lastPatchUtc?: string | null;
  load5?: number | null;
  cpuCount?: number | null;
  lastSuccessUtc?: string | null;
  lastError?: string | null;
  hasManagementCredential: boolean;
}

export interface ServerInfo {
  method: CollectionMethod;
  osName?: string | null;
  osVersion?: string | null;
  lastBootUtc?: string | null;
  pendingReboot?: boolean | null;
  lastPatchUtc?: string | null;
  lastPatchId?: string | null;
  cpuCount?: number | null;
  totalMemoryBytes?: number | null;
  load1?: number | null;
  load5?: number | null;
  load15?: number | null;
  lastSuccessUtc?: string | null;
  lastAttemptUtc: string;
  lastError?: string | null;
  eventLogError?: string | null;
}

export interface Volume {
  id: number;
  name: string;
  label?: string | null;
  fileSystem?: string | null;
  totalBytes: number;
  freeBytes: number;
  usedPct: number;
  lastSeenUtc: string;
}

export interface WatchedService {
  id: number;
  serviceName: string;
  displayName?: string | null;
  state: ServiceState;
  startMode?: string | null;
  lastSeenUtc?: string | null;
  lastChangeUtc?: string | null;
}

export interface ServerDetail {
  info?: ServerInfo | null;
  volumes: Volume[];
  services: WatchedService[];
}

export interface SeriesInfo {
  id: number;
  key: string;
  instance: string;
  displayName: string;
}

export interface PagedEvents<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
  totalCapped: boolean;
}

export interface SyslogEntry {
  id: number;
  receivedUtc: string;
  messageUtc?: string | null;
  sourceIp: string;
  deviceId?: number | null;
  deviceName?: string | null;
  facility: number;
  severity: number;
  severityName: string;
  hostname?: string | null;
  appName?: string | null;
  message: string;
}

export interface TrapEntry {
  id: number;
  receivedUtc: string;
  sourceIp: string;
  deviceId?: number | null;
  deviceName?: string | null;
  version: string;
  trapOid: string;
  trapName?: string | null;
  severity: Severity;
  summary: string;
  varbindsJson: string;
}

export interface WindowsEvent {
  id: number;
  timestampUtc: string;
  deviceId: number;
  deviceName?: string | null;
  logName: string;
  provider?: string | null;
  eventId: number;
  level: number;
  message: string;
}

export type Recurrence = 'Once' | 'Daily' | 'Weekly' | 'Cron';

export interface MaintenanceWindow {
  id: number;
  name: string;
  isEnabled: boolean;
  scope: RuleScope;
  recurrence: Recurrence;
  startUtc?: string | null;
  endUtc?: string | null;
  startTimeLocal?: string | null;
  daysOfWeek?: string | null;
  cronExpression?: string | null;
  durationMin: number;
  reason?: string | null;
  createdBy?: string | null;
  updatedUtc: string;
  isActive: boolean;
  activeUntilUtc?: string | null;
  nextStartUtc?: string | null;
  schedule: string;
  devicesInScope: number;
}

export interface AlertingSettings {
  flapThreshold: number;
  flapWindowMin: number;
  flapStableMin: number;
  escalateCriticalAfterMin: number;
  escalateMajorAfterMin: number;
  repeatSoundEveryMin: number;
  dependencySuppression: boolean;
}

export interface ReceiverSettings {
  syslogEnabled: boolean;
  syslogUdpPort: number;
  syslogTcpEnabled: boolean;
  syslogTcpPort: number;
  trapsEnabled: boolean;
  trapPort: number;
  acceptUnknownSources: boolean;
  maxMessagesPerSecond: number;
}

export interface ServerMonitoringSettings {
  winRmPort: number;
  winRmHttps: boolean;
  dcomFallback: boolean;
  readEventLogs: boolean;
  includeWarnings: boolean;
  maxEventsPerPoll: number;
  sshPort: number;
  timeoutSec: number;
}

export type LdapSecurity = 'Ldaps' | 'StartTls' | 'None';
export type LdapBindMethod = 'Negotiate' | 'Simple';

export interface LdapSettings {
  enabled: boolean;
  servers: string[];
  port: number;
  security: LdapSecurity;
  bindMethod: LdapBindMethod;
  validateCertificate: boolean;
  domain?: string | null;
  baseDn?: string | null;
  timeoutSec: number;
  roleMappings: { groupDn: string; role: Role }[];
}

export type SmtpSecurity = 'None' | 'StartTls' | 'SslOnConnect';

export interface NotificationSettings {
  notifyOnResolve: boolean;
  notifyOnEscalation: boolean;
  eventLogEnabled: boolean;
  eventLogMinSeverity: Severity;
  syslogForwardEnabled: boolean;
  syslogForwardHost?: string | null;
  syslogForwardPort: number;
  syslogForwardProtocol: 'Udp' | 'Tcp';
  syslogForwardMinSeverity: Severity;
  scriptEnabled: boolean;
  scriptFile?: string | null;
  scriptMinSeverity: Severity;
  scriptTimeoutSec: number;
  smtpEnabled: boolean;
  smtpHost?: string | null;
  smtpPort: number;
  smtpSecurity: SmtpSecurity;
  smtpUsername?: string | null;
  hasSmtpPassword: boolean;
  smtpFrom?: string | null;
  smtpTo?: string | null;
  smtpMinSeverity: Severity;
  scriptsFolder: string;
}

export interface ChannelStatus {
  channel: string;
  sent: number;
  failed: number;
  lastSuccessUtc?: string | null;
  lastErrorUtc?: string | null;
  lastError?: string | null;
}
// ---------------------------------------------------------------- Phase 3: VMware

export interface VcenterSummary {
  deviceId: number;
  name: string;
  ipAddress: string;
  lastPollUtc?: string | null;
  hosts: number;
  hostsConnected: number;
  vms: number;
  vmsPoweredOn: number;
  datastores: number;
  snapshots: number;
  alarms: number;
}

export interface VmwareHost {
  id: number;
  sourceDeviceId: number;
  moRef: string;
  name: string;
  cluster?: string | null;
  connectionState: string;
  inMaintenance: boolean;
  overallStatus?: string | null;
  vendor?: string | null;
  model?: string | null;
  product?: string | null;
  cpuCores?: number | null;
  cpuPct?: number | null;
  memPct?: number | null;
  memTotalBytes?: number | null;
  bootTimeUtc?: string | null;
  sensorWarnings: number;
  sensorAlarms: number;
  deviceId?: number | null;
  vmCount: number;
  vmsPoweredOn: number;
  lastSeenUtc: string;
}

export interface VmwareVm {
  id: number;
  sourceDeviceId: number;
  moRef: string;
  name: string;
  hostName?: string | null;
  powerState: string;
  guestOs?: string | null;
  ipAddress?: string | null;
  toolsRunningStatus?: string | null;
  toolsVersionStatus?: string | null;
  overallStatus?: string | null;
  numCpu?: number | null;
  memoryMb?: number | null;
  cpuPct?: number | null;
  cpuReadyPct?: number | null;
  memPct?: number | null;
  balloonedMb?: number | null;
  committedBytes?: number | null;
  uncommittedBytes?: number | null;
  guestDiskMaxPct?: number | null;
  datastores?: string | null;
  snapshotCount: number;
  bootTimeUtc?: string | null;
  deviceId?: number | null;
  lastSeenUtc: string;
}

export interface VmwareDatastore {
  id: number;
  sourceDeviceId: number;
  moRef: string;
  name: string;
  type?: string | null;
  capacityBytes: number;
  freeBytes: number;
  usedPct: number;
  uncommittedBytes?: number | null;
  accessible: boolean;
  maintenanceMode?: string | null;
  hostCount: number;
  vmCount: number;
  lastSeenUtc: string;
}

export interface VmwareSnapshot {
  id: number;
  sourceDeviceId: number;
  vmMoRef: string;
  vmName: string;
  vmDeviceId?: number | null;
  name: string;
  description?: string | null;
  createdUtc: string;
  ageDays: number;
  sizeBytes?: number | null;
  isCurrent: boolean;
}

export interface VmwareAlarm {
  id: number;
  sourceDeviceId: number;
  alarmName: string;
  entityMoRef: string;
  entityName?: string | null;
  status: string;
  acknowledged: boolean;
  triggeredUtc: string;
}

export interface VmwareEvent {
  id: number;
  sourceDeviceId: number;
  sourceName?: string | null;
  eventKey: number;
  timestampUtc: string;
  eventType: string;
  severity: Severity;
  userName?: string | null;
  entityName?: string | null;
  message: string;
}

export interface VmwareSettings {
  importEvents: boolean;
  importAlarms: boolean;
  collectCpuReady: boolean;
  trustOnFirstUse: boolean;
  timeoutSec: number;
  maxEventsPerPoll: number;
}

// ---- Domain controllers (Phase 4)

export interface DcSummary {
  deviceId: number;
  name: string;
  ipAddress: string;
  status: DeviceStatus;
  lastPollUtc?: string | null;
  domain?: string | null;
  site?: string | null;
  dcName?: string | null;
  isGlobalCatalog: boolean;
  roles?: string | null;
  pdcEmulator?: string | null;
  clockOffsetSec?: number | null;
  skewToPdcSec?: number | null;
  lastReplicationCheckUtc?: string | null;
  replicationError?: string | null;
  securityLogError?: string | null;
  replicationLinks: number;
  replicationFailing: number;
  servicesDown: number;
  events24h: number;
  failedLogons24h: number;
  lockouts24h: number;
  groupChanges24h: number;
  services?: DcServiceState[];
  ports?: DcPortState[];
}

export interface DcServiceState {
  name: string;
  displayName?: string | null;
  state: 'Unknown' | 'Running' | 'Stopped' | 'StartPending' | 'StopPending' | 'Paused' | 'NotFound' | string;
  lastChangeUtc?: string | null;
}

export interface DcPortState {
  service: string;
  port: number;
  error?: string | null;
  ms?: number | null;
}

export interface DcOverview {
  controllers: DcSummary[];
  useForwardedEvents: boolean;
  forwardedError?: string | null;
  forwardedLastReadUtc?: string | null;
  forwardedEventsRead: number;
}

export interface DcSecurityEvent {
  id: number;
  timestampUtc: string;
  deviceId: number;
  deviceName?: string | null;
  eventId: number;
  eventName: string;
  category: string;
  severity: Severity;
  subjectUser?: string | null;
  targetUser?: string | null;
  groupName?: string | null;
  objectName?: string | null;
  sourceIp?: string | null;
  workstation?: string | null;
  logonType?: number | null;
  status?: string | null;
  subStatus?: string | null;
  message: string;
}

export interface DcLockout {
  id: number;
  timestampUtc: string;
  deviceId: number;
  deviceName?: string | null;
  user?: string | null;
  callerComputer?: string | null;
  failuresBefore: number;
  lastFailureSource?: string | null;
  unlockedUtc?: string | null;
  unlockedBy?: string | null;
}

export interface DcGroupChange {
  id: number;
  timestampUtc: string;
  deviceId: number;
  deviceName?: string | null;
  eventId: number;
  added: boolean;
  group?: string | null;
  member?: string | null;
  changedBy?: string | null;
  privileged: boolean;
}

export interface DcReplicationLink {
  id: number;
  deviceId: number;
  deviceName: string;
  partner: string;
  namingContext: string;
  lastSuccessUtc?: string | null;
  lastAttemptUtc?: string | null;
  lastResult: number;
  consecutiveFailures: number;
  lastMessage?: string | null;
  lastSeenUtc: string;
}

export interface DcTopEntry {
  key: string;
  count: number;
  lastUtc: string;
}

export interface DcEventId {
  id: number;
  name: string;
  category: string;
}

export interface SavedSearch<Q = Record<string, string>> {
  id: number;
  page: string;
  name: string;
  query: Q;
  isShared: boolean;
  isOwn: boolean;
  createdUtc: string;
}

export interface DcSettings {
  readSecurityLog: boolean;
  useForwardedEvents: boolean;
  forwardedDcs: string[];
  replicationStaleHours: number;
  checkServicePorts: boolean;
  successfulLogonIgnoreSources: string[];
  storeSuccessfulLogons: boolean;
  successfulLogonTypes: number[];
  excludeComputerAccounts: boolean;
  maxSecurityEventsPerPoll: number;
  replicationCheckMin: number;
  workingDays: string;
  workingHoursStart: string;
  workingHoursEnd: string;
  privilegedGroups: string[];
}

// ---- Phase 5: reports, backups, self-monitoring, topology

export type ReportTypeName = 'Availability' | 'Alerts' | 'Interfaces' | 'Capacity' | 'Security' | 'Inventory';
export type ReportFormatName = 'Pdf' | 'Csv';
export type ReportFrequency = 'Daily' | 'Weekly' | 'Monthly';

export interface ReportTypeInfo {
  type: ReportTypeName;
  name: string;
  description: string;
  hasPeriod: boolean;
}

export interface ReportFileInfo {
  path: string;
  name: string;
  sizeBytes: number;
  createdUtc: string;
}

export interface ReportSchedule {
  id: number;
  name: string;
  isEnabled: boolean;
  reportType: ReportTypeName;
  format: ReportFormatName;
  frequency: ReportFrequency;
  timeOfDay: string;
  dayOfWeek: number;
  dayOfMonth: number;
  groupId?: number | null;
  emailTo?: string | null;
  lastRunUtc?: string | null;
  lastStatus?: string | null;
  lastFile?: string | null;
  nextRunUtc: string;
  createdBy?: string | null;
}

export interface BackupInfo {
  name: string;
  sizeBytes: number;
  createdUtc: string;
  includesMetrics: boolean;
  version?: string | null;
  reason?: string | null;
}

export interface BackupSettings {
  enabled: boolean;
  dailyAt: string;
  includeMetrics: boolean;
  keepCount: number;
  folder?: string | null;
}

export interface BackupOverview {
  items: BackupInfo[];
  settings: BackupSettings;
  defaultFolder: string;
  nextRunUtc?: string | null;
  lastAttemptUtc?: string | null;
  lastError?: string | null;
  running: boolean;
}

export interface SelfCheck {
  name: string;
  ok: boolean;
  detail: string;
}

export interface SelfMonitorSettings {
  minFreeDiskPct: number;
  minFreeDiskGb: number;
  certificateWarnDays: number;
  maxBackupAgeHours: number;
}

export interface TopologyNode {
  id: number;
  name: string;
  ipAddress: string;
  type: DeviceType;
  status: DeviceStatus;
  worstAlert?: Severity | null;
  parentId?: number | null;
  groupPath?: string | null;
  unreachableVia?: string | null;
}
