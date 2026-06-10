/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { TeamMember } from '@/lib/types';

const now = Date.now();
const days = (n: number) => new Date(now - n * 86_400_000).toISOString();

export const initialTeamMembers: TeamMember[] = [
  {
    id: 'usr-001',
    email: 'owner@acme.com',
    firstName: 'Alex',
    lastName: 'Morgan',
    role: 'OWNER',
    emailVerified: true,
    isActive: true,
    lastLoginAt: days(0),
    createdAt: days(120),
  },
  {
    id: 'usr-002',
    email: 'sarah@acme.com',
    firstName: 'Sarah',
    lastName: 'Chen',
    role: 'ADMIN',
    emailVerified: true,
    isActive: true,
    lastLoginAt: days(0),
    createdAt: days(90),
  },
  {
    id: 'usr-003',
    email: 'james@acme.com',
    firstName: 'James',
    lastName: 'Wilson',
    role: 'ADMIN',
    emailVerified: true,
    isActive: true,
    lastLoginAt: days(1),
    createdAt: days(60),
  },
  {
    id: 'usr-004',
    email: 'viewer@acme.com',
    firstName: 'Emily',
    lastName: 'Park',
    role: 'VIEWER',
    emailVerified: true,
    isActive: true,
    lastLoginAt: days(3),
    createdAt: days(30),
  },
  {
    id: 'usr-005',
    email: 'contractor@acme.com',
    firstName: 'Mike',
    lastName: 'Torres',
    role: 'VIEWER',
    emailVerified: true,
    isActive: false,
    lastLoginAt: days(14),
    createdAt: days(45),
  },
];

export const initialPendingInvitations = [
  {
    id: 'inv-001',
    email: 'newhire@acme.com',
    role: 'VIEWER',
    expiresAt: days(-5),
  },
];
