import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { registerUser } from '@/lib/services/auth';
import { setAuthCookies } from '@/lib/auth/cookies';
import { parseBody } from '@/lib/api/validate';
import { registerSchema } from '@/lib/validations/api-auth';
import { assertRateLimit } from '@/lib/api/rate-limit';

export async function POST(req: Request) {
  try {
    assertRateLimit(req, 'auth/register', 5, 60_000);
    const body = await parseBody(req, registerSchema);
    const result = await registerUser(body);
    await setAuthCookies(result.tokens.accessToken, result.tokens.refreshToken);
    return ok({ user: result.user, organization: result.organization });
  } catch (err) {
    return handleRouteError(err);
  }
}
