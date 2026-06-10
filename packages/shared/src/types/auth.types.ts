import { UserRole } from '../enums';

export interface JwtPayload {
  sub: string;
  email: string;
  organizationId: string;
  role: UserRole;
  type: 'access' | 'refresh';
}

export interface AuthTokens {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
}

export interface AuthUser {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  role: UserRole;
  organizationId: string;
  emailVerified: boolean;
}
