import { z } from 'zod';

export const createMaintenanceSchema = z
  .object({
    title: z.string().trim().min(1).max(200),
    monitorId: z.string().uuid().optional().nullable(),
    startTime: z.string().min(1),
    endTime: z.string().min(1),
    reason: z.string().max(500).optional().nullable(),
    pauseAlerts: z.boolean().optional().default(true),
  })
  .refine(
    (data) => new Date(data.endTime).getTime() > new Date(data.startTime).getTime(),
    { message: 'End time must be after start time', path: ['endTime'] },
  );

export type CreateMaintenanceInput = z.infer<typeof createMaintenanceSchema>;
