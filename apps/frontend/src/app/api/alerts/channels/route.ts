import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { parseBody } from '@/lib/api/validate';
import { requireSession, requireRole } from '@/lib/auth/session';
import { UserRole } from '@netwatch/shared';
import { listAlertChannels, createAlertChannel } from '@/lib/services/domain';
import { createAlertChannelSchema } from '@/lib/validations/api-alerts';

export async function GET() {
  try {
    const session = await requireSession();
    const data = await listAlertChannels(session.organizationId);
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}

export async function POST(req: Request) {
  try {
    const session = await requireRole(UserRole.ADMIN);
    const body = await parseBody(req, createAlertChannelSchema);
    const channel = await createAlertChannel(session.organizationId, session.email, body);
    return ok(channel);
  } catch (err) {
    return handleRouteError(err);
  }
}
