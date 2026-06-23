import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { parseBody } from '@/lib/api/validate';
import { requireSession, requireRole } from '@/lib/auth/session';
import { getMonitorDetail, updateMonitor, deleteMonitor } from '@/lib/services/domain';
import { updateMonitorApiSchema } from '@/lib/validations/api-monitor';
import { UserRole } from '@netwatch/shared';

export async function GET(_req: Request, { params }: { params: Promise<{ id: string }> }) {
  try {
    const session = await requireSession();
    const { id } = await params;
    const data = await getMonitorDetail(session.organizationId, id);
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}

export async function PATCH(req: Request, { params }: { params: Promise<{ id: string }> }) {
  try {
    const session = await requireRole(UserRole.ADMIN);
    const { id } = await params;
    const body = await parseBody(req, updateMonitorApiSchema);
    const monitor = await updateMonitor(session.organizationId, id, session.email, body);
    return ok(monitor);
  } catch (err) {
    return handleRouteError(err);
  }
}

export async function DELETE(_req: Request, { params }: { params: Promise<{ id: string }> }) {
  try {
    const session = await requireRole(UserRole.ADMIN);
    const { id } = await params;
    await deleteMonitor(session.organizationId, id, session.email, session.role);
    return ok({ message: 'Monitor deleted' });
  } catch (err) {
    return handleRouteError(err);
  }
}
