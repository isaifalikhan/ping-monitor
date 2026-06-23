import { cookies } from 'next/headers';
import { COOKIE_NAMES } from '@netwatch/shared';
import { ok } from '@/lib/api/response';
import { logoutUser } from '@/lib/services/auth';
import { clearAuthCookies } from '@/lib/auth/cookies';

export async function POST() {
  const cookieStore = await cookies();
  const refreshToken = cookieStore.get(COOKIE_NAMES.REFRESH_TOKEN)?.value;
  await logoutUser(refreshToken);
  await clearAuthCookies();
  return ok({ message: 'Logged out' });
}
