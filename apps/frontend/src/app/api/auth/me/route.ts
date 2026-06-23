import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireSession } from '@/lib/auth/session';
import { getMe } from '@/lib/services/auth';

export async function GET() {
  try {
    const session = await requireSession();
    const data = await getMe(session.sub);
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}
