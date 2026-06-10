import Link from 'next/link';
import { Activity } from 'lucide-react';

interface AuthLayoutProps {
  children: React.ReactNode;
  title: string;
  description: string;
}

export function AuthLayout({ children, title, description }: AuthLayoutProps) {
  return (
    <div className="min-h-screen grid lg:grid-cols-2">
      <div className="hidden lg:flex flex-col justify-between bg-primary p-12 text-primary-foreground">
        <Link href="/" className="flex items-center gap-2 text-xl font-bold">
          <Activity className="h-8 w-8" />
          NetWatch
        </Link>
        <div className="space-y-4">
          <h2 className="text-3xl font-bold leading-tight">
            Enterprise Network Monitoring
          </h2>
          <p className="text-primary-foreground/80 text-lg">
            Monitor websites, APIs, servers, and infrastructure in real-time.
            Detect incidents before they impact your users.
          </p>
        </div>
        <p className="text-sm text-primary-foreground/60">
          Trusted by teams worldwide for uptime monitoring
        </p>
      </div>

      <div className="flex items-center justify-center p-8">
        <div className="w-full max-w-md space-y-6">
          <div className="lg:hidden flex items-center justify-center gap-2 mb-8">
            <Activity className="h-8 w-8 text-primary" />
            <span className="text-2xl font-bold">NetWatch</span>
          </div>
          <div className="space-y-2 text-center lg:text-left">
            <h1 className="text-2xl font-bold tracking-tight">{title}</h1>
            <p className="text-muted-foreground">{description}</p>
          </div>
          {children}
        </div>
      </div>
    </div>
  );
}
