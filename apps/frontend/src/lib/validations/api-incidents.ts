import { z } from 'zod';
import { INCIDENT_SEVERITIES } from '@/lib/incidents/timeline';

export const resolveIncidentSchema = z.object({
  rootCause: z.string().max(2000).optional().nullable(),
  notes: z.string().max(2000).optional().nullable(),
});

export const updateIncidentSchema = z.object({
  assignedUserId: z.string().uuid().nullable().optional(),
  severity: z.enum(INCIDENT_SEVERITIES).optional(),
  rootCause: z.string().max(2000).optional().nullable(),
  notes: z.string().max(2000).optional().nullable(),
  note: z.string().min(1).max(1000).optional(),
});
