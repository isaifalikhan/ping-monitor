import { AuthError } from '@/lib/auth/session';
import { fail } from '@/lib/api/response';
import { ValidationError } from '@/lib/api/validate';
import { RateLimitError } from '@/lib/api/rate-limit';

const isProd = process.env.NODE_ENV === 'production';

function safeMessage(err: unknown): string {
  if (err instanceof AuthError) return err.message;
  if (err instanceof ValidationError) return err.message;
  if (err instanceof RateLimitError) return err.message;
  if (err instanceof Error) {
    if (isProd) return 'Request failed';
    return err.message;
  }
  return isProd ? 'Internal server error' : 'Unknown error';
}

function statusFor(err: unknown): number {
  if (err instanceof AuthError) return err.status;
  if (err instanceof RateLimitError) return 429;
  if (err instanceof ValidationError) return 400;
  return 400;
}

export function handleRouteError(err: unknown) {
  const status = statusFor(err);
  const response = fail(safeMessage(err), status);
  if (err instanceof RateLimitError) {
    response.headers.set('Retry-After', '60');
  }
  return response;
}
