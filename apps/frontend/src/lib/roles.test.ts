/**
 * @jest-environment node
 */
import { UserRole } from '@netwatch/shared';
import {
  canAssignRole,
  canEditCompanySettings,
  canManageIncidents,
  canManageMonitors,
  canViewAuditLogs,
} from './roles';

describe('role helpers', () => {
  it('restricts company settings to owner', () => {
    expect(canEditCompanySettings(UserRole.OWNER)).toBe(true);
    expect(canEditCompanySettings(UserRole.ADMIN)).toBe(false);
  });

  it('allows monitor management for owner and admin', () => {
    expect(canManageMonitors(UserRole.ADMIN)).toBe(true);
    expect(canManageMonitors(UserRole.ENGINEER)).toBe(false);
  });

  it('allows incident management for engineer and above', () => {
    expect(canManageIncidents(UserRole.ENGINEER)).toBe(true);
    expect(canManageIncidents(UserRole.VIEWER)).toBe(false);
  });

  it('restricts audit logs to owner and admin', () => {
    expect(canViewAuditLogs(UserRole.ADMIN)).toBe(true);
    expect(canViewAuditLogs(UserRole.ENGINEER)).toBe(false);
  });

  it('controls role assignment', () => {
    expect(canAssignRole(UserRole.OWNER, UserRole.ADMIN)).toBe(true);
    expect(canAssignRole(UserRole.ADMIN, UserRole.OWNER)).toBe(false);
    expect(canAssignRole(UserRole.ADMIN, UserRole.ENGINEER)).toBe(true);
  });
});
