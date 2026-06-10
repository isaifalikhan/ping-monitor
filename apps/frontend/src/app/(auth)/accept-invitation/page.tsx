'use client';

import { useSearchParams } from 'next/navigation';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { Suspense } from 'react';
import { AuthLayout } from '@/components/auth/auth-layout';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { useAuth } from '@/hooks/use-auth';
import {
  acceptInvitationSchema,
  type AcceptInvitationFormData,
} from '@/lib/validations/auth';
import { ApiError } from '@/lib/api-client';

function AcceptInvitationForm() {
  const searchParams = useSearchParams();
  const token = searchParams.get('token') || '';
  const { acceptInvitation } = useAuth();

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<AcceptInvitationFormData>({
    resolver: zodResolver(acceptInvitationSchema),
  });

  const onSubmit = (data: AcceptInvitationFormData) => {
    acceptInvitation.mutate({ ...data, token });
  };

  if (!token) {
    return (
      <AuthLayout title="Invalid invitation" description="This invitation link is invalid.">
        <Button className="w-full" disabled>
          Invalid link
        </Button>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout
      title="Accept invitation"
      description="Complete your profile to join the team"
    >
      <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
        <div className="grid grid-cols-2 gap-4">
          <div className="space-y-2">
            <Label htmlFor="firstName">First Name</Label>
            <Input id="firstName" {...register('firstName')} />
            {errors.firstName && (
              <p className="text-sm text-destructive">{errors.firstName.message}</p>
            )}
          </div>
          <div className="space-y-2">
            <Label htmlFor="lastName">Last Name</Label>
            <Input id="lastName" {...register('lastName')} />
            {errors.lastName && (
              <p className="text-sm text-destructive">{errors.lastName.message}</p>
            )}
          </div>
        </div>

        <div className="space-y-2">
          <Label htmlFor="password">Password</Label>
          <Input id="password" type="password" {...register('password')} />
          {errors.password && (
            <p className="text-sm text-destructive">{errors.password.message}</p>
          )}
        </div>

        <div className="space-y-2">
          <Label htmlFor="confirmPassword">Confirm Password</Label>
          <Input id="confirmPassword" type="password" {...register('confirmPassword')} />
          {errors.confirmPassword && (
            <p className="text-sm text-destructive">{errors.confirmPassword.message}</p>
          )}
        </div>

        {acceptInvitation.isError && (
          <p className="text-sm text-destructive">
            {acceptInvitation.error instanceof ApiError
              ? acceptInvitation.error.message
              : 'Failed to accept invitation.'}
          </p>
        )}

        <Button type="submit" className="w-full" disabled={acceptInvitation.isPending}>
          {acceptInvitation.isPending ? 'Joining...' : 'Join team'}
        </Button>
      </form>
    </AuthLayout>
  );
}

export default function AcceptInvitationPage() {
  return (
    <Suspense fallback={<div className="min-h-screen flex items-center justify-center">Loading...</div>}>
      <AcceptInvitationForm />
    </Suspense>
  );
}
