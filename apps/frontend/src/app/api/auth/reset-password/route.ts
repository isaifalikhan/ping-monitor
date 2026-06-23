import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { resetPassword } from '@/lib/services/auth';
import { parseBody } from '@/lib/api/validate';
import { resetPasswordApiSchema } from '@/lib/validations/api-auth';
import { assertRateLimit } from '@/lib/api/rate-limit';

export async function POST(req: Request) {
  try {
    assertRateLimit(req, 'auth/reset-password', 10, 60_000);
    const { token, password } = await parseBody(req, resetPasswordApiSchema);
    await resetPassword(token, password);
    return ok({ message: 'Password reset successfully' });
  } catch (err) {
    return handleRouteError(err);
  }
}
