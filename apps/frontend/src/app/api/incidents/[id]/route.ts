import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireSession, requireRole } from '@/lib/auth/session';
import { parseBody } from '@/lib/api/validate';
import { getIncident, updateIncident } from '@/lib/services/incidents';
import { updateIncidentSchema } from '@/lib/validations/api-incidents';
import { UserRole } from '@netwatch/shared';

export async function GET(_req: Request, { params }: { params: Promise<{ id: string }> }) {
  try {
    const session = await requireSession();
    const { id } = await params;
    const data = await getIncident(session.organizationId, id);
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}

export async function PATCH(req: Request, { params }: { params: Promise<{ id: string }> }) {
  try {
    const session = await requireRole(UserRole.ENGINEER);
    const { id } = await params;
    const body = await parseBody(req, updateIncidentSchema);
    const data = await updateIncident(
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
