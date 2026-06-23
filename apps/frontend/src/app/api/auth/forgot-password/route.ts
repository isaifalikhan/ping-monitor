import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { forgotPassword } from '@/lib/services/auth';
import { parseBody } from '@/lib/api/validate';
import { forgotPasswordSchema } from '@/lib/validations/api-auth';
import { assertRateLimit } from '@/lib/api/rate-limit';

export async function POST(req: Request) {
  try {
    assertRateLimit(req, 'auth/forgot-password', 5, 60_000);
    const { email } = await parseBody(req, forgotPasswordSchema);
    const result = await forgotPassword(email);
    return ok(result);
  } catch (err) {
    return handleRouteError(err);
  }
}
