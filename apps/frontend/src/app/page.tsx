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
            <Link href="/login">
              <Button>Sign in to NetWatch</Button>
            </Link>
          </div>
        </div>
      </header>

      <main>
        <section className="container mx-auto px-4 py-24 text-center">
          <h1 className="text-4xl md:text-6xl font-bold tracking-tight mb-6">
            Internal Infrastructure
            <br />
            <span className="text-primary">Monitoring</span>
          </h1>
          <p className="text-xl text-muted-foreground max-w-2xl mx-auto mb-8">
            Monitor ERP systems, servers, switches, routers, firewalls, APIs, databases,
            and critical internal services from one dashboard.
          </p>
          <div className="flex gap-4 justify-center">
            <Link href="/login">
              <Button size="lg" className="gap-2">
                Open Dashboard <ArrowRight className="h-4 w-4" />
              </Button>
            </Link>
          </div>
        </section>

        <section className="container mx-auto px-4 py-16 grid md:grid-cols-3 gap-8">
          {[
            {
              icon: Zap,
              title: 'Infrastructure Checks',
              description: 'HTTP, HTTPS, TCP, DNS, SSL, and availability monitoring for internal devices.',
            },
            {
              icon: Shield,
              title: 'Role-Based Access',
              description: 'Owner, Admin, Engineer, and Viewer roles for your IT operations team.',
            },
            {
              icon: BarChart3,
              title: 'Incidents & Reports',
              description: 'Automatic incidents, alert delivery, uptime tracking, and operational reports.',
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
