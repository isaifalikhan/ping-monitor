import { UserRole } from '@netwatch/shared';

/** Human-readable labels for internal NetWatch roles */
export const ROLE_LABELS: Record<UserRole, string> = {
  [UserRole.OWNER]: 'Owner',
  [UserRole.ADMIN]: 'Admin',
  [UserRole.ENGINEER]: 'Engineer',
  [UserRole.VIEWER]: 'Viewer',
};

export const INVITABLE_ROLES: UserRole[] = [UserRole.ADMIN, UserRole.ENGINEER, UserRole.VIEWER];

/**
 * Internal permissions matrix (single company).
 * Enforced on API routes via requireRole and role helpers.
 */
export const PERMISSIONS_MATRIX = [
  { permission: 'View dashboard & monitors', owner: true, admin: true, engineer: true, viewer: true },
  { permission: 'Create / edit / delete monitors', owner: true, admin: true, engineer: false, viewer: false },
  { permission: 'Manage alerts & maintenance', owner: true, admin: true, engineer: false, viewer: false },
  { permission: 'Acknowledge & resolve incidents', owner: true, admin: true, engineer: true, viewer: false },
  { permission: 'View reports', owner: true, admin: true, engineer: true, viewer: true },
  { permission: 'View audit logs', owner: true, admin: true, engineer: false, viewer: false },
  { permission: 'Invite team members', owner: true, admin: true, engineer: false, viewer: false },
  { permission: 'Company settings', owner: true, admin: false, engineer: false, viewer: false },
] as const;

export type PermissionColumn = 'owner' | 'admin' | 'engineer' | 'viewer';

export const PERMISSION_COLUMNS: { key: PermissionColumn; label: string }[] = [
  { key: 'owner', label: 'Owner' },
  { key: 'admin', label: 'Admin' },
  { key: 'engineer', label: 'Engineer' },
  { key: 'viewer', label: 'Viewer' },
];

export function canManageTeam(role?: string): boolean {
  return role === UserRole.OWNER || role === UserRole.ADMIN;
}

export function canEditCompanySettings(role?: string): boolean {
  return role === UserRole.OWNER;
}

export function canManageMonitors(role?: string): boolean {
  return role === UserRole.OWNER || role === UserRole.ADMIN;
}

export function canManageAlerts(role?: string): boolean {
  return role === UserRole.OWNER || role === UserRole.ADMIN;
}

export function canViewAuditLogs(role?: string): boolean {
  return role === UserRole.OWNER || role === UserRole.ADMIN;
}

export function canManageMaintenance(role?: string): boolean {
  return role === UserRole.OWNER || role === UserRole.ADMIN;
}

export function canManageIncidents(role?: string): boolean {
  return (
    role === UserRole.OWNER ||
    role === UserRole.ADMIN ||
    role === UserRole.ENGINEER
  );
}

export function canAssignRole(actorRole: UserRole, targetRole: UserRole): boolean {
  if (targetRole === UserRole.OWNER) {
    return actorRole === UserRole.OWNER;
  }
  return actorRole === UserRole.OWNER || actorRole === UserRole.ADMIN;
}
