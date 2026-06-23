'use client';

import { useEffect } from 'react';
import { useRouter } from 'next/navigation';

/** Internal NetWatch accounts are verified on creation — no separate email verification step. */
export default function VerifyEmailPage() {
  const router = useRouter();
  useEffect(() => {
    router.replace('/login');
  }, [router]);
  return null;
}
