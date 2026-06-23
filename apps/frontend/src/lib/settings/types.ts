export const DEFAULT_SETTINGS_PREFS = {
  defaultCheckInterval: 60,
  defaultTimeout: 30,
  retentionDays: 90,
  statusPageSlug: '',
  sessionTimeoutMinutes: 480,
  requireMfa: false,
  ipAllowlist: '',
  brandPrimaryColor: '#3b82f6',
  brandLogoUrl: '',
  slackWebhook: '',
  pagerdutyKey: '',
  datadogApiKey: '',
} as const;

export type OrganizationSettingsPrefs = {
  defaultCheckInterval: number;
  defaultTimeout: number;
  retentionDays: number;
  statusPageSlug: string;
  sessionTimeoutMinutes: number;
  requireMfa: boolean;
  ipAllowlist: string;
  brandPrimaryColor: string;
  brandLogoUrl: string;
  slackWebhook: string;
  pagerdutyKey: string;
  datadogApiKey: string;
};

export type OrganizationSettings = {
  latencyThresholdMs: number;
  notificationEmail: string;
  timezone: string;
  enablePublicStatusPage: boolean;
} & OrganizationSettingsPrefs;

export type SettingsResponse = {
  organization: { id: string; name: string; slug: string } | null;
  settings: OrganizationSettings;
};
