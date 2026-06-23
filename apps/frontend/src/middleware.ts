import { NextResponse } from 'next/server';
import type { NextRequest } from 'next/server';
import { checkRateLimit } from '@/lib/api/rate-limit';
import { applySecurityHeaders } from '@/lib/api/security-headers';

function clientIp(req: NextRequest) {
  return (
    req.headers.get('x-forwarded-for')?.split(',')[0]?.trim() ||
    req.headers.get('x-real-ip') ||
    'unknown'
  );
}

export function middleware(request: NextRequest) {
  const { pathname } = request.nextUrl;

  if (pathname.startsWith('/api/')) {
    const exempt =
      pathname.startsWith('/api/cron/') || pathname === '/api/health';

    if (!exempt) {
      const ip = clientIp(request);
      const isAuth = pathname.startsWith('/api/auth/');
      const isPublic = pathname.startsWith('/api/public/');
      const limit = isAuth ? 30 : isPublic ? 60 : 150;
      const key = `mw:${isAuth ? 'auth' : isPublic ? 'public' : 'api'}:${ip}`;

      if (!checkRateLimit(key, limit, 60_000)) {
        const res = NextResponse.json(
          { success: false, error: 'Too many requests. Please try again later.' },
          { status: 429 },
        );
        res.headers.set('Retry-After', '60');
        return applySecurityHeaders(res);
      }
    }

    const res = NextResponse.next();
    res.headers.set('Cache-Control', 'no-store');
    return applySecurityHeaders(res);
  }

  return applySecurityHeaders(NextResponse.next());
}

export const config = {
  matcher: ['/api/:path*'],
};
