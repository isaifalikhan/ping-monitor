import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireRole } from '@/lib/auth/session';
import { listAuditLogs } from '@/lib/services/domain';
import { UserRole } from '@netwatch/shared';
import { guardApiRequest } from '@/lib/api/request-guard';
import { RATE_LIMITS } from '@/lib/api/rate-limit';

export async function GET(req: Request) {
  try {
    guardApiRequest(req, 'audit-logs', RATE_LIMITS.read.limit, RATE_LIMITS.read.windowMs);
    const session = await requireRole(UserRole.ADMIN);
    const category = new URL(req.url).searchParams.get('category') ?? undefined;
    const data = await listAuditLogs(session.organizationId, category);
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}
