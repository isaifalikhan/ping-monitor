/**
 * MOCK DATA ONLY - demo preview for internal NetWatch
 */
import type { TeamMember } from '@/lib/types';

const now = Date.now();
const mins = (n: number) => new Date(now - n * 60_000).toISOString();
const hours = (n: number) => new Date(now - n * 3_600_000).toISOString();
const days = (n: number) => new Date(now - n * 86_400_000).toISOString();

export const initialTeamMembers: TeamMember[] = [
  { id: 'usr-001', email: 'ops.lead@company.internal', firstName: 'Alex', lastName: 'Morgan', role: 'OWNER', title: 'IT Operations Lead', emailVerified: true, isActive: true, lastLoginAt: mins(15), createdAt: days(400), avatarColor: 'hsl(220 70% 50%)', status: 'online' },
  { id: 'usr-002', email: 'infra.admin@company.internal', firstName: 'Sarah', lastName: 'Chen', role: 'ADMIN', title: 'Infrastructure Admin', emailVerified: true, isActive: true, lastLoginAt: mins(30), createdAt: days(300), avatarColor: 'hsl(280 70% 50%)', status: 'online' },
  { id: 'usr-003', email: 'network.eng@company.internal', firstName: 'Marcus', lastName: 'Rivera', role: 'ENGINEER', title: 'Network Engineer', emailVerified: true, isActive: true, lastLoginAt: hours(2), createdAt: days(200), avatarColor: 'hsl(142 70% 40%)', status: 'away' },
  { id: 'usr-004', email: 'noc.viewer@company.internal', firstName: 'James', lastName: 'Okonkwo', role: 'VIEWER', title: 'NOC Analyst', emailVerified: true, isActive: true, lastLoginAt: hours(1), createdAt: days(150), avatarColor: 'hsl(38 90% 50%)', status: 'online' },
  { id: 'usr-005', email: 'erp.support@company.internal', firstName: 'Emily', lastName: 'Park', role: 'VIEWER', title: 'ERP Support', emailVerified: true, isActive: true, lastLoginAt: hours(4), createdAt: days(90), avatarColor: 'hsl(340 70% 50%)', status: 'offline' },
  { id: 'usr-006', email: 'systems.eng@company.internal', firstName: 'David', lastName: 'Kim', role: 'ENGINEER', title: 'Systems Engineer', emailVerified: true, isActive: true, lastLoginAt: days(1), createdAt: days(60), avatarColor: 'hsl(200 70% 45%)', status: 'offline' },
];

export const initialPendingInvitations = [
  { id: 'inv-001', email: 'new.engineer@company.internal', role: 'ENGINEER', expiresAt: days(-5) },
];

export { PERMISSIONS_MATRIX as permissionsMatrix } from '@/lib/roles';
