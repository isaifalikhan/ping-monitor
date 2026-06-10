import { randomBytes } from 'crypto';

export function generateSecureToken(bytes = 32): string {
  return randomBytes(bytes).toString('hex');
}

export function getExpiresAt(hours: number): Date {
  const date = new Date();
  date.setHours(date.getHours() + hours);
  return date;
}

export function getExpiresAtDays(days: number): Date {
  const date = new Date();
  date.setDate(date.getDate() + days);
  return date;
}
