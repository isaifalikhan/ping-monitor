import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireSession } from '@/lib/auth/session';
import { getAlertStats } from '@/lib/services/domain';

export async function GET() {
  try {
    const session = await requireSession();
    const data = await getAlertStats(session.organizationId);
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}
