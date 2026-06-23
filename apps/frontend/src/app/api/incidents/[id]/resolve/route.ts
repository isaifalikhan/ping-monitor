import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireRole } from '@/lib/auth/session';
import { parseBody } from '@/lib/api/validate';
import { resolveIncident } from '@/lib/services/incidents';
import { resolveIncidentSchema } from '@/lib/validations/api-incidents';
import { UserRole } from '@netwatch/shared';

export async function PATCH(req: Request, { params }: { params: Promise<{ id: string }> }) {
  try {
    const session = await requireRole(UserRole.ENGINEER);
    const { id } = await params;
    const body = await parseBody(req, resolveIncidentSchema);
    const data = await resolveIncident(
      session.organizationId,
      id,
      session.sub,
      session.email,
      body,
    );
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}
