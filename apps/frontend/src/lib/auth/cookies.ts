import { cookies } from 'next/headers';
import { COOKIE_NAMES } from '@netwatch/shared';

const isProd = process.env.NODE_ENV === 'production';

export async function setAuthCookies(accessToken: string, refreshToken: string) {
  const cookieStore = await cookies();
  const base = {
    httpOnly: true,
    secure: isProd,
    sameSite: 'lax' as const,
    path: '/',
  };
  cookieStore.set(COOKIE_NAMES.ACCESS_TOKEN, accessToken, {
    ...base,
    maxAge: 60 * 15,
  });
  cookieStore.set(COOKIE_NAMES.REFRESH_TOKEN, refreshToken, {
    ...base,
    maxAge: 60 * 60 * 24 * 7,
  });
}

export async function clearAuthCookies() {
  const cookieStore = await cookies();
  cookieStore.delete(COOKIE_NAMES.ACCESS_TOKEN);
  cookieStore.delete(COOKIE_NAMES.REFRESH_TOKEN);
}

export async function getRefreshTokenFromCookies(): Promise<string | undefined> {
  const cookieStore = await cookies();
  return cookieStore.get(COOKIE_NAMES.REFRESH_TOKEN)?.value;
}
