export enum UserRole {
  OWNER = 'OWNER',
  ADMIN = 'ADMIN',
  ENGINEER = 'ENGINEER',
  VIEWER = 'VIEWER',
}

export const USER_ROLE_HIERARCHY: Record<UserRole, number> = {
  [UserRole.OWNER]: 4,
  [UserRole.ADMIN]: 3,
  [UserRole.ENGINEER]: 2,
  [UserRole.VIEWER]: 1,
};

export function hasMinimumRole(userRole: UserRole, requiredRole: UserRole): boolean {
  return USER_ROLE_HIERARCHY[userRole] >= USER_ROLE_HIERARCHY[requiredRole];
}
