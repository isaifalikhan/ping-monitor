/**
 * MOCK DATA ONLY - replace with real API later
 */
export const initialSettings = {
  latencyThresholdMs: 500,
  notificationEmail: 'alerts@acme-corp.com',
  timezone: 'America/New_York',
  defaultCheckInterval: 60,
  defaultTimeout: 30,
  retentionDays: 90,
  enablePublicStatusPage: true,
  statusPageSlug: 'acme-corp',
  sessionTimeoutMinutes: 480,
  requireMfa: false,
  ipAllowlist: '',
  brandPrimaryColor: '#3b82f6',
  brandLogoUrl: '',
  slackWebhook: 'https://hooks.slack.com/services/demo',
  pagerdutyKey: '••••••••••••demo',
  datadogApiKey: '',
};

export const initialOrganization = {
  id: 'org-demo',
  name: 'Acme Corporation',
  slug: 'acme-corp',
};
