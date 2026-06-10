export const API_PREFIX = '/api/v1';

export const DEFAULT_PAGE_SIZE = 20;
export const MAX_PAGE_SIZE = 100;

export const PASSWORD_MIN_LENGTH = 8;
export const BCRYPT_ROUNDS = 12;

export const TOKEN_BYTES = 32;
export const EMAIL_VERIFICATION_EXPIRES_HOURS = 24;
export const PASSWORD_RESET_EXPIRES_HOURS = 1;
export const INVITATION_EXPIRES_DAYS = 7;

export const COOKIE_NAMES = {
  ACCESS_TOKEN: 'netwatch_access_token',
  REFRESH_TOKEN: 'netwatch_refresh_token',
} as const;

export const WS_EVENTS = {
  MONITOR_STATUS_CHANGED: 'monitor.status_changed',
  INCIDENT_CREATED: 'incident.created',
  INCIDENT_RESOLVED: 'incident.resolved',
  ALERT_SENT: 'alert.sent',
  CHECK_COMPLETED: 'check.completed',
  DASHBOARD_UPDATED: 'dashboard.updated',
} as const;
