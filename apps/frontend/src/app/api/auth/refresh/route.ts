import { ok } from '@/lib/api/response';
import { handleRouteError } from '@/lib/api/handler';
import { refreshSession } from '@/lib/services/auth';
import { setAuthCookies, getRefreshTokenFromCookies } from '@/lib/auth/cookies';
import { assertRateLimit } from '@/lib/api/rate-limit';

export async function POST(req: Request) {
  try {
    assertRateLimit(req, 'auth/refresh', 30, 60_000);
    const refreshToken = (await getRefreshTokenFromCookies()) || '';
    if (!refreshToken) throw new Error('No refresh token');
    const result = await refreshSession(refreshToken);
    await setAuthCookies(result.tokens.accessToken, result.tokens.refreshToken);
    return ok({ user: result.user, organization: result.organization });
  } catch (err) {
    return handleRouteError(err);
  }
}
