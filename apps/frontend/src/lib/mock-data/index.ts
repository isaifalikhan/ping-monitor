/**
 * MOCK DATA ONLY - replace with real API later
 * Centralized demo seed data for all NetWatch feature pages.
 */
export {
  initialMonitors,
  initialMonitorChecks,
  initialMonitorAlertHistory,
} from './monitors';
export { initialIncidents } from './incidents';
export {
  initialAlertChannels,
  initialAlertRules,
  initialAlertDeliveries,
  initialRecentAlerts,
  alertStats,
} from './alerts';
export { initialTeamMembers, initialPendingInvitations, permissionsMatrix } from './team';
export { initialMaintenanceWindows } from './maintenance';
export { initialSettings, initialOrganization } from './settings';
export { initialAuditLogs } from './audit-logs';
export {
  statusPageConfig,
  statusPageComponents,
  statusPageUptimeHistory,
  statusPageIncidents,
  statusPageMaintenance,
} from './status-page';
export {
  responseTimeTrend,
  uptimeTrend,
  uptime24h,
  incidentTrend,
  monitorDistribution,
  statusDistribution,
} from './dashboard';
export { reportTemplates } from './reports';
