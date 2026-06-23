import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireRole } from '@/lib/auth/session';
import { UserRole } from '@netwatch/shared';
import { updateTeamMember, removeTeamMember } from '@/lib/services/auth';
import { parseBody } from '@/lib/api/validate';
import { updateMemberSchema } from '@/lib/validations/api-auth';

export async function PATCH(req: Request, { params }: { params: Promise<{ memberId: string }> }) {
  try {
    const session = await requireRole(UserRole.ADMIN);
    const { memberId } = await params;
    const body = await parseBody(req, updateMemberSchema);
    const user = await updateTeamMember(
      session.organizationId,
      session.sub,
      session.role,
      memberId,
      body,
    );
    return ok(user);
  } catch (err) {
    return handleRouteError(err);
  }
}

export async function DELETE(_req: Request, { params }: { params: Promise<{ memberId: string }> }) {
  try {
    const session = await requireRole(UserRole.ADMIN);
    const { memberId } = await params;
    await removeTeamMember(session.organizationId, session.sub, memberId);
    return ok({ message: 'Member removed' });
  } catch (err) {
    return handleRouteError(err);
  }
}
