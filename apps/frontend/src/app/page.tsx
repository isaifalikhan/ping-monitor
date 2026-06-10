import Link from 'next/link';
import { Activity, ArrowRight, Shield, Zap, BarChart3 } from 'lucide-react';
import { Button } from '@/components/ui/button';

export default function HomePage() {
  return (
    <div className="min-h-screen bg-background">
      <header className="border-b">
        <div className="container mx-auto flex h-16 items-center justify-between px-4">
          <div className="flex items-center gap-2 font-bold text-xl">
            <Activity className="h-6 w-6 text-primary" />
            NetWatch
          </div>
          <div className="flex items-center gap-4">
            <Link href="/login">
              <Button variant="ghost">Sign in</Button>
            </Link>
            <Link href="/register">
              <Button>Get Started</Button>
            </Link>
          </div>
        </div>
      </header>

      <main>
        <section className="container mx-auto px-4 py-24 text-center">
          <h1 className="text-4xl md:text-6xl font-bold tracking-tight mb-6">
            Monitor Your Infrastructure
            <br />
            <span className="text-primary">With Confidence</span>
          </h1>
          <p className="text-xl text-muted-foreground max-w-2xl mx-auto mb-8">
            Real-time monitoring for websites, APIs, servers, and network devices.
            Detect incidents before they impact your users.
          </p>
          <div className="flex gap-4 justify-center">
            <Link href="/register">
              <Button size="lg" className="gap-2">
                Start Free Trial <ArrowRight className="h-4 w-4" />
              </Button>
            </Link>
            <Link href="/login">
              <Button size="lg" variant="outline">
                Sign In
              </Button>
            </Link>
          </div>
        </section>

        <section className="container mx-auto px-4 py-16 grid md:grid-cols-3 gap-8">
          {[
            {
              icon: Zap,
              title: 'Real-time Monitoring',
              description: 'Continuous checks with instant alerts when issues arise.',
            },
            {
              icon: Shield,
              title: 'Enterprise Security',
              description: 'Multi-tenant isolation, role-based access, and audit logs.',
            },
            {
              icon: BarChart3,
              title: 'Advanced Analytics',
              description: 'Uptime reports, SLA tracking, and performance insights.',
            },
          ].map((feature) => (
            <div key={feature.title} className="rounded-lg border p-6 space-y-3">
              <feature.icon className="h-10 w-10 text-primary" />
              <h3 className="text-lg font-semibold">{feature.title}</h3>
              <p className="text-muted-foreground">{feature.description}</p>
            </div>
          ))}
        </section>
      </main>
    </div>
  );
}
