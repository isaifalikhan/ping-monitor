'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import {
  Activity,
  LayoutDashboard,
  Monitor,
  AlertTriangle,
  Bell,
  BarChart3,
  Users,
  Settings,
  Wrench,
  ScrollText,
  ExternalLink,
} from 'lucide-react';
import { cn } from '@/lib/utils';

const navItems = [
  { href: '/dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { href: '/monitors', label: 'Monitors', icon: Monitor },
  { href: '/incidents', label: 'Incidents', icon: AlertTriangle },
  { href: '/alerts', label: 'Alerts', icon: Bell },
  { href: '/reports', label: 'Reports', icon: BarChart3 },
  { href: '/team', label: 'Team', icon: Users },
  { href: '/maintenance', label: 'Maintenance', icon: Wrench },
  { href: '/audit-logs', label: 'Audit Logs', icon: ScrollText },
  { href: '/settings', label: 'Settings', icon: Settings },
];

const externalLinks = [
  { href: '/status', label: 'Status Page', icon: ExternalLink },
];

export function Sidebar() {
  const pathname = usePathname();

  return (
    <aside className="hidden lg:flex w-64 flex-col border-r bg-card">
      <div className="flex h-16 items-center gap-2 border-b px-6">
        <Activity className="h-6 w-6 text-primary" />
        <span className="text-lg font-bold">NetWatch</span>
      </div>
      <nav className="flex-1 space-y-1 p-4">
        {navItems.map((item) => {
          const Icon = item.icon;
          const isActive =
            pathname === item.href ||
            (item.href !== '/dashboard' && pathname.startsWith(item.href));
          return (
            <Link
              key={item.href}
              href={item.href}
              className={cn(
                'flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors',
                isActive
                  ? 'bg-primary text-primary-foreground'
                  : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground',
              )}
            >
              <Icon className="h-4 w-4" />
              {item.label}
            </Link>
          );
        })}
      </nav>
      <div className="border-t p-4 space-y-1">
        <p className="px-3 text-xs font-medium text-muted-foreground uppercase tracking-wider mb-2">Public</p>
        {externalLinks.map((item) => {
          const Icon = item.icon;
          return (
            <a
              key={item.href}
              href={item.href}
              target="_blank"
              rel="noopener noreferrer"
              className="flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
            >
              <Icon className="h-4 w-4" />
              {item.label}
            </a>
          );
        })}
      </div>
    </aside>
  );
}
