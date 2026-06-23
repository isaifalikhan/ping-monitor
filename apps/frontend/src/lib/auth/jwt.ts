import jwt, { type SignOptions } from 'jsonwebtoken';
import type { JwtPayload } from '@netwatch/shared';

const DEFAULT_SECRET = 'dev-secret-change-in-production';
const MIN_SECRET_LENGTH = 32;

function getJwtSecret(): string {
  const secret = process.env.JWT_SECRET || DEFAULT_SECRET;
  if (process.env.NODE_ENV === 'production') {
    if (!process.env.JWT_SECRET || secret === DEFAULT_SECRET) {
      throw new Error('JWT_SECRET must be set to a strong value in production');
    }
    if (secret.length < MIN_SECRET_LENGTH) {
      throw new Error(`JWT_SECRET must be at least ${MIN_SECRET_LENGTH} characters in production`);
    }
  }
  return secret;
}

const JWT_EXPIRES_IN = process.env.JWT_EXPIRES_IN || '15m';
const REFRESH_EXPIRES_DAYS = 7;

export function signAccessToken(payload: JwtPayload, expiresIn?: string | number): string {
  const options: SignOptions = {
    expiresIn: (expiresIn ?? JWT_EXPIRES_IN) as SignOptions['expiresIn'],
  };
  return jwt.sign(payload, getJwtSecret(), options);
}

export function verifyAccessToken(token: string): JwtPayload {
  return jwt.verify(token, getJwtSecret()) as JwtPayload;
}

export function getRefreshExpiresAt(expiresAt?: Date) {
  return expiresAt ?? new Date(Date.now() + REFRESH_EXPIRES_DAYS * 24 * 60 * 60 * 1000);
}
