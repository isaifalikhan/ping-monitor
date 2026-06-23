import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireSession, requireRole } from '@/lib/auth/session';
import { UserRole } from '@netwatch/shared';
import { listMaintenance, createMaintenance } from '@/lib/services/domain';
import { parseBody } from '@/lib/api/validate';
import { createMaintenanceSchema } from '@/lib/validations/api-maintenance';
import { guardApiRequest } from '@/lib/api/request-guard';
import { RATE_LIMITS } from '@/lib/api/rate-limit';
import { logAuditFromRequest } from '@/lib/audit/logger';

export async function GET(req: Request) {
  try {
    guardApiRequest(req, 'maintenance/list', RATE_LIMITS.read.limit, RATE_LIMITS.read.windowMs);
    const session = await requireSession();
    const data = await listMaintenance(session.organizationId);
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}

export async function POST(req: Request) {
  try {
    guardApiRequest(req, 'maintenance/create', RATE_LIMITS.write.limit, RATE_LIMITS.write.windowMs);
    const session = await requireRole(UserRole.ADMIN);
    const body = await parseBody(req, createMaintenanceSchema);
    const window = await createMaintenance(session.organizationId, body);
    await logAuditFromRequest(req, {
      organizationId: session.organizationId,
      category: 'MAINTENANCE',
      action: `Scheduled maintenance "${window.title}"`,
      actor: session.email,
      actorUserId: session.sub,
      target: window.id,
    });
    return ok(window);
  } catch (err) {
    return handleRouteError(err);
  }
}
