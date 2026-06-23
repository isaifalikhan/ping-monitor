import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { requireSession } from '@/lib/auth/session';
import { getReportSummary } from '@/lib/services/domain';

export async function GET(req: Request) {
  try {
    const session = await requireSession();
    const { searchParams } = new URL(req.url);
    const data = await getReportSummary(
      session.organizationId,
      searchParams.get('from') ?? undefined,
      searchParams.get('to') ?? undefined,
    );
    return ok(data);
  } catch (err) {
    return handleRouteError(err);
  }
}
