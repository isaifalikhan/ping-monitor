'use client';

import { useState, type FormEvent } from 'react';
import { useRouter } from 'next/navigation';
import { Activity, LogOut, Moon, Sun, Search } from 'lucide-react';
import { useTheme } from 'next-themes';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { useAuth } from '@/hooks/use-auth';
import { useAuthStore } from '@/stores/auth-store';
import { DEMO_MODE } from '@/lib/demo-mode';
import { MobileNav } from './mobile-nav';

export function DashboardHeader() {
  const router = useRouter();
  const { theme, setTheme } = useTheme();
  const { logout } = useAuth();
  const { user, organization } = useAuthStore();
  const [search, setSearch] = useState('');

  const onSearch = (e: FormEvent) => {
    e.preventDefault();
    const q = search.trim();
    if (q) router.push(`/monitors?search=${encodeURIComponent(q)}`);
  };

  return (
    <header className="sticky top-0 z-10 flex h-16 items-center gap-4 border-b bg-background/95 px-4 backdrop-blur supports-[backdrop-filter]:bg-background/60 lg:px-6">
      <MobileNav />

      <div className="flex items-center gap-2 font-bold text-xl lg:hidden">
        <Activity className="h-6 w-6 text-primary" />
        NetWatch
      </div>

      <form onSubmit={onSearch} className="relative flex-1 max-w-md hidden sm:block">
        <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          placeholder="Search monitors, incidents..."
          className="pl-9"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          title="Search monitors by name, target, or tag"
        />
      </form>

      <div className="ml-auto flex items-center gap-2">
        {DEMO_MODE && (
          <Badge variant="warning" className="hidden sm:inline-flex">
            Demo Mode
          </Badge>
        )}
        <Button
          variant="ghost"
          size="icon"
          onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}
          title="Toggle theme"
        >
          <Sun className="h-4 w-4 rotate-0 scale-100 transition-all dark:-rotate-90 dark:scale-0" />
          <Moon className="absolute h-4 w-4 rotate-90 scale-0 transition-all dark:rotate-0 dark:scale-100" />
        </Button>

        <div className="hidden md:block text-right text-sm">
          <p className="font-medium">
            {user?.firstName} {user?.lastName}
          </p>
          <p className="text-xs text-muted-foreground">{organization?.name}</p>
        </div>

        <Button
          variant="ghost"
          size="sm"
          onClick={() => logout.mutate()}
          disabled={logout.isPending}
          title="Sign out"
        >
          <LogOut className="h-4 w-4 mr-2" />
          <span className="hidden sm:inline">Sign out</span>
        </Button>
      </div>
    </header>
  );
}
