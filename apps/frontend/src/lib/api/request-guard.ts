import { assertRateLimit, getClientIp } from '@/lib/api/rate-limit';

export type RequestContext = {
  ip: string;
};

/** Per-route rate limit (in addition to middleware baseline). */
export function guardApiRequest(
  req: Request,
  route: string,
  limit = 60,
  windowMs = 60_000,
): RequestContext {
  assertRateLimit(req, route, limit, windowMs);
  return { ip: getClientIp(req) };
}
