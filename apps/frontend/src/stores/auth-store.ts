import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { AuthUser, OrganizationProfile } from '@netwatch/shared';

interface AuthState {
  user: AuthUser | null;
  organization: OrganizationProfile | null;
  isAuthenticated: boolean;
  setAuth: (user: AuthUser, organization: OrganizationProfile) => void;
  clearAuth: () => void;
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      user: null,
      organization: null,
      isAuthenticated: false,
      setAuth: (user, organization) =>
        set({ user, organization, isAuthenticated: true }),
      clearAuth: () =>
        set({ user: null, organization: null, isAuthenticated: false }),
    }),
    {
      name: 'netwatch-auth',
      partialize: (state) => ({
        user: state.user,
        organization: state.organization,
        isAuthenticated: state.isAuthenticated,
      }),
    },
  ),
);
