import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireSession } from '@/lib/auth/session';
import { listIncidents } from '@/lib/services/domain';

export async function GET(req: Request) {
  try {
    const session = await requireSession();
    const status = new URL(req.url).searchParams.get('status') ?? undefined;
    const data = await listIncidents(session.organizationId, status);
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}
