/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { TeamMember } from '@/lib/types';

const now = Date.now();
const mins = (n: number) => new Date(now - n * 60_000).toISOString();
const hours = (n: number) => new Date(now - n * 3_600_000).toISOString();
const days = (n: number) => new Date(now - n * 86_400_000).toISOString();

export const initialTeamMembers: TeamMember[] = [
  { id: 'usr-001', email: 'alex.morgan@acme-corp.com', firstName: 'Alex', lastName: 'Morgan', role: 'OWNER', title: 'VP Engineering', emailVerified: true, isActive: true, lastLoginAt: mins(15), createdAt: days(400), avatarColor: 'hsl(220 70% 50%)', status: 'online' },
  { id: 'usr-002', email: 'sarah.chen@acme-corp.com', firstName: 'Sarah', lastName: 'Chen', role: 'ADMIN', title: 'Platform Lead', emailVerified: true, isActive: true, lastLoginAt: mins(30), createdAt: days(300), avatarColor: 'hsl(280 70% 50%)', status: 'online' },
  { id: 'usr-003', email: 'marcus.rivera@acme-corp.com', firstName: 'Marcus', lastName: 'Rivera', role: 'ADMIN', title: 'Network Engineer', emailVerified: true, isActive: true, lastLoginAt: hours(2), createdAt: days(200), avatarColor: 'hsl(142 70% 40%)', status: 'away' },
  { id: 'usr-004', email: 'james.okonkwo@acme-corp.com', firstName: 'James', lastName: 'Okonkwo', role: 'VIEWER', title: 'Support Engineer', emailVerified: true, isActive: true, lastLoginAt: hours(1), createdAt: days(150), avatarColor: 'hsl(38 90% 50%)', status: 'online' },
  { id: 'usr-005', email: 'emily.park@acme-corp.com', firstName: 'Emily', lastName: 'Park', role: 'VIEWER', title: 'Support Engineer', emailVerified: true, isActive: true, lastLoginAt: hours(4), createdAt: days(90), avatarColor: 'hsl(340 70% 50%)', status: 'offline' },
  { id: 'usr-006', email: 'david.kim@acme-corp.com', firstName: 'David', lastName: 'Kim', role: 'VIEWER', title: 'Network Engineer', emailVerified: true, isActive: true, lastLoginAt: days(1), createdAt: days(60), avatarColor: 'hsl(200 70% 45%)', status: 'offline' },
];

export const initialPendingInvitations = [
  { id: 'inv-001', email: 'newhire@acme-corp.com', role: 'VIEWER', expiresAt: days(-5) },
];

export const permissionsMatrix = [
  { permission: 'View monitors & dashboards', owner: true, admin: true, network: true, support: true, viewer: true },
  { permission: 'Create / edit monitors', owner: true, admin: true, network: true, support: false, viewer: false },
  { permission: 'Acknowledge incidents', owner: true, admin: true, network: true, support: true, viewer: false },
  { permission: 'Manage alert channels', owner: true, admin: true, network: false, support: false, viewer: false },
  { permission: 'Invite team members', owner: true, admin: true, network: false, support: false, viewer: false },
  { permission: 'Organization settings', owner: true, admin: false, network: false, support: false, viewer: false },
  { permission: 'Export reports', owner: true, admin: true, network: true, support: true, viewer: true },
];
