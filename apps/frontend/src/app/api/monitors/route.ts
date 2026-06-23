import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { parseBody } from '@/lib/api/validate';
import { requireSession, requireRole } from '@/lib/auth/session';
import { listMonitors, createMonitor } from '@/lib/services/domain';
import { createMonitorApiSchema } from '@/lib/validations/api-monitor';
import { UserRole } from '@netwatch/shared';
import { guardApiRequest } from '@/lib/api/request-guard';
import { RATE_LIMITS } from '@/lib/api/rate-limit';

export async function GET(req: Request) {
  try {
    guardApiRequest(req, 'monitors/list', RATE_LIMITS.read.limit, RATE_LIMITS.read.windowMs);
    const session = await requireSession();
    const { searchParams } = new URL(req.url);
    const data = await listMonitors(session.organizationId, {
      page: Number(searchParams.get('page') ?? 1),
      limit: Number(searchParams.get('limit') ?? 100),
      search: searchParams.get('search') ?? undefined,
      status: searchParams.get('status') ?? undefined,
      type: searchParams.get('type') ?? undefined,
    });
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}

export async function POST(req: Request) {
  try {
    guardApiRequest(req, 'monitors/create', RATE_LIMITS.write.limit, RATE_LIMITS.write.windowMs);
    const session = await requireRole(UserRole.ADMIN);
    const body = await parseBody(req, createMonitorApiSchema);
    const monitor = await createMonitor(session.organizationId, session.email, body);
    return ok(monitor);
  } catch (err) {
    return handleRouteError(err);
  }
}
