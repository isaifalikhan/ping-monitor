const buckets = new Map<string, { count: number; resetAt: number }>();

export function getClientIp(req: Request): string {
  return (
    req.headers.get('x-forwarded-for')?.split(',')[0]?.trim() ||
    req.headers.get('x-real-ip') ||
    'unknown'
  );
}

/** Returns true if the request is within the rate limit. */
export function checkRateLimit(key: string, limit: number, windowMs: number): boolean {
  const now = Date.now();
  const entry = buckets.get(key);
  if (!entry || now > entry.resetAt) {
    buckets.set(key, { count: 1, resetAt: now + windowMs });
    return true;
  }
  if (entry.count >= limit) return false;
  entry.count += 1;
  return true;
}

export function assertRateLimit(req: Request, route: string, limit = 20, windowMs = 60_000) {
  const key = `${route}:${getClientIp(req)}`;
  if (!checkRateLimit(key, limit, windowMs)) {
    throw new RateLimitError('Too many requests. Please try again later.');
  }
}

export class RateLimitError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'RateLimitError';
  }
}

export const RATE_LIMITS = {
  auth: { limit: 15, windowMs: 60_000 },
  write: { limit: 40, windowMs: 60_000 },
  read: { limit: 120, windowMs: 60_000 },
} as const;
