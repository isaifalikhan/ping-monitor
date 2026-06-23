import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireRole } from '@/lib/auth/session';
import { UserRole } from '@netwatch/shared';
import { testAlertChannel } from '@/lib/services/domain';

export async function POST(_req: Request, { params }: { params: Promise<{ id: string }> }) {
  try {
    const session = await requireRole(UserRole.ADMIN);
    const { id } = await params;
    const result = await testAlertChannel(session.organizationId, id);
    return ok(result);
  } catch (err) {
    return handleRouteError(err);
  }
}
