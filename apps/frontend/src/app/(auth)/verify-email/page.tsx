'use client';

import { useSearchParams } from 'next/navigation';
import Link from 'next/link';
import { useEffect } from 'react';
import { Suspense } from 'react';
import { AuthLayout } from '@/components/auth/auth-layout';
import { Button } from '@/components/ui/button';
import { useAuth } from '@/hooks/use-auth';

function VerifyEmailContent() {
  const searchParams = useSearchParams();
  const token = searchParams.get('token') || '';
  const { verifyEmail } = useAuth();

  useEffect(() => {
    if (token) {
      verifyEmail.mutate(token);
    }
  }, [token]);

  if (!token) {
    return (
      <AuthLayout title="Invalid link" description="This verification link is invalid.">
        <Link href="/login">
          <Button className="w-full">Sign in</Button>
        </Link>
      </AuthLayout>
    );
  }

  if (verifyEmail.isPending) {
    return (
      <AuthLayout title="Verifying..." description="Please wait while we verify your email.">
        <div className="flex justify-center">
          <div className="h-8 w-8 animate-spin rounded-full border-4 border-primary border-t-transparent" />
        </div>
      </AuthLayout>
    );
  }

  if (verifyEmail.isError) {
    return (
      <AuthLayout
        title="Verification failed"
        description="This link may have expired. Please request a new one."
      >
        <Link href="/dashboard">
          <Button className="w-full">Go to dashboard</Button>
        </Link>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout
      title="Email verified"
      description="Your email has been verified successfully."
    >
      <Link href="/dashboard">
        <Button className="w-full">Continue to dashboard</Button>
      </Link>
    </AuthLayout>
  );
}

export default function VerifyEmailPage() {
  return (
    <Suspense fallback={<div className="min-h-screen flex items-center justify-center">Loading...</div>}>
      <VerifyEmailContent />
    </Suspense>
  );
}
