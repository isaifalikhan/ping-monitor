import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireRole } from '@/lib/auth/session';
import { parseBody } from '@/lib/api/validate';
import { acknowledgeIncident } from '@/lib/services/incidents';
import { UserRole } from '@netwatch/shared';

export async function PATCH(_req: Request, { params }: { params: Promise<{ id: string }> }) {
  try {
    const session = await requireRole(UserRole.ENGINEER);
    const { id } = await params;
    const data = await acknowledgeIncident(
      session.organizationId,
      id,
      session.sub,
      session.email,
    );
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}
