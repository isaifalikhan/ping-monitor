import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { loginUser } from '@/lib/services/auth';
import { setAuthCookies } from '@/lib/auth/cookies';
import { parseBody } from '@/lib/api/validate';
import { loginSchema } from '@/lib/validations/api-auth';
import { assertRateLimit, getClientIp } from '@/lib/api/rate-limit';
import { logFailedLogin } from '@/lib/audit/logger';

export async function POST(req: Request) {
  try {
    assertRateLimit(req, 'auth/login', 15, 60_000);
    const { email, password } = await parseBody(req, loginSchema);
    const ip = getClientIp(req);
    try {
      const result = await loginUser(email, password, { ipAddress: ip });
      await setAuthCookies(result.tokens.accessToken, result.tokens.refreshToken);
      return ok({ user: result.user, organization: result.organization });
    } catch (loginErr) {
      if (loginErr instanceof Error && loginErr.message === 'Invalid credentials') {
        await logFailedLogin(req, email);
      }
      throw loginErr;
    }
  } catch (err) {
    return handleRouteError(err);
  }
}
