'use client';

import { useState } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { Menu, X, Activity, ExternalLink } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

const navItems = [
  { href: '/dashboard', label: 'Dashboard' },
  { href: '/monitors', label: 'Monitors' },
  { href: '/incidents', label: 'Incidents' },
  { href: '/alerts', label: 'Alerts' },
  { href: '/reports', label: 'Reports' },
  { href: '/team', label: 'Team' },
  { href: '/maintenance', label: 'Maintenance' },
  { href: '/audit-logs', label: 'Audit Logs' },
  { href: '/settings', label: 'Settings' },
];

export function MobileNav() {
  const [open, setOpen] = useState(false);
  const pathname = usePathname();

  return (
    <>
      <Button variant="ghost" size="icon" className="lg:hidden" onClick={() => setOpen(true)} title="Open menu">
        <Menu className="h-5 w-5" />
      </Button>

      {open && (
        <div className="fixed inset-0 z-50 lg:hidden">
          <div className="absolute inset-0 bg-black/50" onClick={() => setOpen(false)} />
          <aside className="relative flex h-full w-72 flex-col bg-card border-r shadow-xl">
            <div className="flex h-16 items-center justify-between border-b px-4">
              <div className="flex items-center gap-2 font-bold">
                <Activity className="h-5 w-5 text-primary" />
                NetWatch
              </div>
              <Button variant="ghost" size="icon" onClick={() => setOpen(false)}>
                <X className="h-5 w-5" />
              </Button>
            </div>
            <nav className="flex-1 overflow-y-auto p-3 space-y-1">
              {navItems.map((item) => {
                const isActive =
                  pathname === item.href ||
                  (item.href !== '/dashboard' && pathname.startsWith(item.href));
                return (
                  <Link
                    key={item.href}
                    href={item.href}
                    onClick={() => setOpen(false)}
                    className={cn(
                      'block rounded-lg px-3 py-2.5 text-sm font-medium transition-colors',
                      isActive
                        ? 'bg-primary text-primary-foreground'
                        : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground',
                    )}
                  >
                    {item.label}
                  </Link>
                );
              })}
            </nav>
            <div className="border-t p-3">
              <a
                href="/status"
                target="_blank"
                rel="noopener noreferrer"
                className="flex items-center gap-2 rounded-lg px-3 py-2 text-sm text-muted-foreground hover:bg-accent"
              >
                <ExternalLink className="h-4 w-4" />
                Status Page
              </a>
            </div>
          </aside>
        </div>
      )}
    </>
  );
}
