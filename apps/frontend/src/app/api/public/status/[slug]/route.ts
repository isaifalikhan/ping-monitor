import { ok, fail } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { getPublicStatus } from '@/lib/services/domain';
import { assertRateLimit } from '@/lib/api/rate-limit';

export async function GET(req: Request, { params }: { params: Promise<{ slug: string }> }) {
  try {
    assertRateLimit(req, 'public/status', 120, 60_000);
    const { slug } = await params;
    const data = await getPublicStatus(slug);
    return ok(data);
  } catch (err) {
    if (err instanceof Error && err.message.includes('not found')) {
      return fail(err.message, 404);
    }
    return handleRouteError(err);
  }
}
