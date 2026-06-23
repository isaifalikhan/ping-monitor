import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireSession, requireRole } from '@/lib/auth/session';
import { getSettings, updateSettings } from '@/lib/services/domain';
import { UserRole } from '@netwatch/shared';
import { parseBody } from '@/lib/api/validate';
import { updateSettingsSchema } from '@/lib/validations/api-settings';
import { guardApiRequest } from '@/lib/api/request-guard';
import { RATE_LIMITS } from '@/lib/api/rate-limit';
import { getClientIp } from '@/lib/api/rate-limit';

export async function GET(req: Request) {
  try {
    guardApiRequest(req, 'settings/get', RATE_LIMITS.read.limit, RATE_LIMITS.read.windowMs);
    const session = await requireSession();
    const data = await getSettings(session.organizationId);
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}

export async function PATCH(req: Request) {
  try {
    guardApiRequest(req, 'settings/patch', RATE_LIMITS.write.limit, RATE_LIMITS.write.windowMs);
    const session = await requireRole(UserRole.OWNER);
    const body = await parseBody(req, updateSettingsSchema);
    const data = await updateSettings(session.organizationId, session.email, body, {
      ipAddress: getClientIp(req),
      actorUserId: session.sub,
    });
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}
