'use client';

import { useEffect } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRouter } from 'next/navigation';
import { apiClient } from '@/lib/api-client';
import { useAuthStore } from '@/stores/auth-store';
import type {
  LoginFormData,
  RegisterFormData,
  ForgotPasswordFormData,
  ResetPasswordFormData,
  AcceptInvitationFormData,
} from '@/lib/validations/auth';
import { AuthUser, OrganizationProfile } from '@netwatch/shared';

interface AuthResponse {
  user: AuthUser;
  organization: OrganizationProfile;
  tokens?: { accessToken: string; refreshToken: string; expiresIn: number };
}

export function useAuth() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { user, organization, isAuthenticated, setAuth, clearAuth } = useAuthStore();

  const loginMutation = useMutation({
    mutationFn: (data: LoginFormData) =>
      apiClient.post<AuthResponse>('/auth/login', data),
    onSuccess: (data) => {
      setAuth(data.user, data.organization);
      queryClient.invalidateQueries({ queryKey: ['profile'] });
      router.push('/dashboard');
    },
  });

  const registerMutation = useMutation({
    mutationFn: (data: RegisterFormData) =>
      apiClient.post<AuthResponse>('/auth/register', data),
    onSuccess: (data) => {
      setAuth(data.user, data.organization);
      router.push('/dashboard');
    },
  });

  const logoutMutation = useMutation({
    mutationFn: () => apiClient.post('/auth/logout'),
    onSuccess: () => {
      clearAuth();
      queryClient.clear();
      router.push('/login');
    },
  });

  const forgotPasswordMutation = useMutation({
    mutationFn: (data: ForgotPasswordFormData) =>
      apiClient.post('/auth/forgot-password', data),
  });

  const resetPasswordMutation = useMutation({
    mutationFn: (data: ResetPasswordFormData & { token: string }) =>
      apiClient.post('/auth/reset-password', {
        token: data.token,
        password: data.password,
      }),
    onSuccess: () => router.push('/login'),
  });

  const verifyEmailMutation = useMutation({
    mutationFn: (token: string) =>
      apiClient.post('/auth/verify-email', { token }),
  });

  const acceptInvitationMutation = useMutation({
    mutationFn: (data: AcceptInvitationFormData & { token: string }) =>
      apiClient.post<AuthResponse>('/auth/accept-invitation', {
        token: data.token,
        firstName: data.firstName,
        lastName: data.lastName,
        password: data.password,
      }),
    onSuccess: (data) => {
      setAuth(data.user, data.organization);
      router.push('/dashboard');
    },
  });

  const profileQuery = useQuery({
    queryKey: ['profile'],
    queryFn: () =>
      apiClient.get<{ user: AuthUser; organization: OrganizationProfile }>('/auth/me'),
    enabled: isAuthenticated,
    retry: false,
  });

  // Keep local auth state in sync with server (e.g. after email verification bypass)
  useEffect(() => {
    if (profileQuery.data?.user && profileQuery.data?.organization) {
      setAuth(profileQuery.data.user, profileQuery.data.organization);
    }
  }, [profileQuery.data, setAuth]);

  return {
    user,
    organization,
    isAuthenticated,
    login: loginMutation,
    register: registerMutation,
    logout: logoutMutation,
    forgotPassword: forgotPasswordMutation,
    resetPassword: resetPasswordMutation,
    verifyEmail: verifyEmailMutation,
    acceptInvitation: acceptInvitationMutation,
    profile: profileQuery,
  };
}
