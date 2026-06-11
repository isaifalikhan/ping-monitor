/**
 * MOCK DATA ONLY - replace with real API later
 */
import type { MaintenanceWindow } from '@/lib/types';

const now = Date.now();
const hours = (n: number) => new Date(now + n * 3_600_000).toISOString();
const hoursAgo = (n: number) => new Date(now - n * 3_600_000).toISOString();
const daysAgo = (n: number) => new Date(now - n * 86_400_000).toISOString();

export const initialMaintenanceWindows: MaintenanceWindow[] = [
  {
    id: 'mw-001', title: 'PostgreSQL 16 upgrade',
    monitor: { id: 'mon-007', name: 'PostgreSQL Cluster' },
    startTime: hours(24), endTime: hours(28),
    reason: 'Major version upgrade with connection pool migration',
    pauseAlerts: true, status: 'UPCOMING', createdAt: daysAgo(3),
  },
  {
    id: 'mw-002', title: 'CDN edge node replacement — US-East',
    monitor: { id: 'mon-012', name: 'CDN Edge — Global' },
    startTime: hours(72), endTime: hours(74),
    reason: 'Hardware refresh on edge nodes',
    pauseAlerts: true, status: 'UPCOMING', createdAt: daysAgo(2),
  },
  {
    id: 'mw-003', title: 'Internal ERP patch deployment',
    monitor: { id: 'mon-006', name: 'Internal ERP' },
    startTime: hoursAgo(2), endTime: hours(1),
    reason: 'SAP security patch KB-2024-0892',
    pauseAlerts: true, status: 'ACTIVE', createdAt: daysAgo(1),
  },
  {
    id: 'mw-004', title: 'SSL certificate renewal — portal',
    monitor: { id: 'mon-010', name: 'SSL Certificate — portal.acme-corp.com' },
    startTime: hours(168), endTime: hours(170),
    reason: 'Automated cert renewal via Let\'s Encrypt',
    pauseAlerts: false, status: 'UPCOMING', createdAt: daysAgo(1),
  },
  {
    id: 'mw-005', title: 'Redis cluster memory optimization',
    monitor: { id: 'mon-008', name: 'Redis Cache' },
    startTime: daysAgo(7), endTime: daysAgo(7),
    reason: 'Memory defragmentation and key eviction policy update',
    pauseAlerts: true, status: 'COMPLETED', createdAt: daysAgo(10),
  },
  {
    id: 'mw-006', title: 'AWS load balancer configuration',
    monitor: { id: 'mon-002', name: 'AWS Production Server' },
    startTime: daysAgo(14), endTime: daysAgo(14),
    reason: 'ALB rule updates for new microservices',
    pauseAlerts: true, status: 'COMPLETED', createdAt: daysAgo(16),
  },
];
