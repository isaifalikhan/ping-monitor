import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { parseBody } from '@/lib/api/validate';
import { requireSession, requireRole } from '@/lib/auth/session';
import { UserRole } from '@netwatch/shared';
import { listAlertRules, createAlertRule } from '@/lib/services/domain';
import { createAlertRuleSchema } from '@/lib/validations/api-alerts';

export async function GET() {
  try {
    const session = await requireSession();
    const data = await listAlertRules(session.organizationId);
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}

export async function POST(req: Request) {
  try {
    const session = await requireRole(UserRole.ADMIN);
    const body = await parseBody(req, createAlertRuleSchema);
    const rule = await createAlertRule(session.organizationId, body);
    return ok(rule);
  } catch (err) {
    return handleRouteError(err);
  }
}
