/**
 * @jest-environment node
 */
import { assertRateLimit, checkRateLimit, getClientIp, RateLimitError } from './rate-limit';

describe('rate limit', () => {
  it('extracts client IP from forwarded headers', () => {
    const req = new Request('http://localhost/api/test', {
      headers: { 'x-forwarded-for': '198.51.100.10, 10.0.0.1' },
    });
    expect(getClientIp(req)).toBe('198.51.100.10');
  });

  it('allows requests within the limit', () => {
    const key = `test-allow-${Date.now()}`;
    expect(checkRateLimit(key, 3, 60_000)).toBe(true);
    expect(checkRateLimit(key, 3, 60_000)).toBe(true);
    expect(checkRateLimit(key, 3, 60_000)).toBe(true);
  });

  it('blocks requests over the limit', () => {
    const key = `test-block-${Date.now()}`;
    expect(checkRateLimit(key, 2, 60_000)).toBe(true);
    expect(checkRateLimit(key, 2, 60_000)).toBe(true);
    expect(checkRateLimit(key, 2, 60_000)).toBe(false);
  });

  it('throws RateLimitError from assertRateLimit', () => {
    const req = new Request('http://localhost/api/auth/login', {
      headers: { 'x-real-ip': `block-ip-${Date.now()}` },
    });
    const route = `/auth/login-${Date.now()}`;

    assertRateLimit(req, route, 1, 60_000);
    expect(() => assertRateLimit(req, route, 1, 60_000)).toThrow(RateLimitError);
  });
});
