import { ok, fail } from '@/lib/api/response';
import { runDueChecks } from '@/lib/monitoring/run-checks';

export const maxDuration = 60;

export async function GET(req: Request) {
  const cronSecret = process.env.CRON_SECRET;
  if (process.env.NODE_ENV === 'production' && !cronSecret) {
    return fail('CRON_SECRET must be configured in production', 500);
  }

  const authHeader = req.headers.get('authorization');
  if (cronSecret && authHeader !== `Bearer ${cronSecret}`) {
    return fail('Unauthorized', 401);
  }

  const result = await runDueChecks();
  return ok(result);
}
