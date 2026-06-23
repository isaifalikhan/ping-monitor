import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireRole } from '@/lib/auth/session';
import { UserRole } from '@netwatch/shared';
import { cancelMaintenance } from '@/lib/services/domain';
import { guardApiRequest } from '@/lib/api/request-guard';
import { RATE_LIMITS } from '@/lib/api/rate-limit';
import { logAuditFromRequest } from '@/lib/audit/logger';

export async function DELETE(
  req: Request,
  { params }: { params: Promise<{ id: string }> },
) {
  try {
    guardApiRequest(req, 'maintenance/cancel', RATE_LIMITS.write.limit, RATE_LIMITS.write.windowMs);
    const session = await requireRole(UserRole.ADMIN);
    const { id } = await params;
    const data = await cancelMaintenance(session.organizationId, id);
    await logAuditFromRequest(req, {
      organizationId: session.organizationId,
      category: 'MAINTENANCE',
      action:
        data.action === 'deleted'
          ? 'Cancelled scheduled maintenance window'
          : 'Ended active maintenance window early',
      actor: session.email,
      actorUserId: session.sub,
      target: id,
    });
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}
