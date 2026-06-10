import { UserRole, hasMinimumRole } from './user-role.enum';

describe('hasMinimumRole', () => {
  it('should return true when user has exact role', () => {
    expect(hasMinimumRole(UserRole.ADMIN, UserRole.ADMIN)).toBe(true);
  });

  it('should return true when user has higher role', () => {
    expect(hasMinimumRole(UserRole.OWNER, UserRole.ADMIN)).toBe(true);
    expect(hasMinimumRole(UserRole.OWNER, UserRole.VIEWER)).toBe(true);
    expect(hasMinimumRole(UserRole.ADMIN, UserRole.VIEWER)).toBe(true);
  });

  it('should return false when user has lower role', () => {
    expect(hasMinimumRole(UserRole.VIEWER, UserRole.ADMIN)).toBe(false);
    expect(hasMinimumRole(UserRole.ADMIN, UserRole.OWNER)).toBe(false);
  });
});
