import { ok } from '@/lib/api/response';
import { UserRole } from '@netwatch/shared';
import { handleRouteError } from '@/lib/api/handler';
import { requireRole } from '@/lib/auth/session';
import { inviteMember } from '@/lib/services/auth';
import { parseBody } from '@/lib/api/validate';
import { inviteMemberSchema } from '@/lib/validations/api-auth';
import { assertRateLimit } from '@/lib/api/rate-limit';

export async function POST(req: Request) {
  try {
    assertRateLimit(req, 'auth/invite', 10, 60_000);
    const session = await requireRole(UserRole.ADMIN);
    const body = await parseBody(req, inviteMemberSchema);
    const result = await inviteMember(session.organizationId, session.sub, body);
    return ok(result);
  } catch (err) {
    return handleRouteError(err);
  }
}
