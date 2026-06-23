import { ok } from '@/lib/api/response';
import { getSupabaseAdmin } from '@/lib/db/supabase';
import { assertRateLimit, RateLimitError } from '@/lib/api/rate-limit';
import { handleRouteError } from '@/lib/api/handler';

export async function GET(req: Request) {
  try {
    assertRateLimit(req, 'health', 60, 60_000);
    const db = getSupabaseAdmin();
    const { error } = await db.from('organizations').select('id').limit(1);
    if (error) throw error;
    return ok({ status: 'ok', timestamp: new Date().toISOString() });
  } catch (err) {
    if (err instanceof RateLimitError) {
      return handleRouteError(err);
    }
    return ok({ status: 'degraded', timestamp: new Date().toISOString() });
  }
}
