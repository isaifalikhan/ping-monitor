export enum UserRole {
  OWNER = 'OWNER',
  ADMIN = 'ADMIN',
  VIEWER = 'VIEWER',
}

export const USER_ROLE_HIERARCHY: Record<UserRole, number> = {
  [UserRole.OWNER]: 3,
  [UserRole.ADMIN]: 2,
  [UserRole.VIEWER]: 1,
};

export function hasMinimumRole(userRole: UserRole, requiredRole: UserRole): boolean {
  return USER_ROLE_HIERARCHY[userRole] >= USER_ROLE_HIERARCHY[requiredRole];
}
