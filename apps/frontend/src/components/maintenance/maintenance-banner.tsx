'use client';

import Link from 'next/link';
import { Wrench, BellOff } from 'lucide-react';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import type { MaintenanceWindow } from '@/lib/types';
import { countMonitorsWithPausedAlerts } from '@/lib/maintenance/utils';
import type { Monitor } from '@/lib/types';

type MaintenanceBannerProps = {
  activeWindows: MaintenanceWindow[];
  monitors?: Monitor[];
  variant?: 'default' | 'compact';
};

export function MaintenanceBanner({
  activeWindows,
  monitors = [],
  variant = 'default',
}: MaintenanceBannerProps) {
  if (!activeWindows.length) return null;

  const pausedCount = countMonitorsWithPausedAlerts(monitors, activeWindows);
  const orgWide = activeWindows.filter((w) => !w.monitor);
  const scoped = activeWindows.filter((w) => w.monitor);

  if (variant === 'compact') {
    return (
      <div className="flex flex-wrap items-center gap-2 rounded-lg border border-amber-500/30 bg-amber-500/10 px-4 py-3 text-sm">
        <Wrench className="h-4 w-4 text-amber-500 shrink-0" />
        <span>
          <strong>{activeWindows.length}</strong> active maintenance window
          {activeWindows.length === 1 ? '' : 's'}
          {pausedCount > 0 && (
            <>
              {' '}
              · alerts paused for <strong>{pausedCount}</strong> monitor
              {pausedCount === 1 ? '' : 's'}
            </>
          )}
        </span>
        <Link href="/maintenance" className="ml-auto">
          <Button size="sm" variant="outline">
            View
          </Button>
        </Link>
      </div>
    );
  }

  return (
    <Card className="border-amber-500/30 bg-amber-500/5">
      <CardContent className="flex flex-col gap-3 p-4 sm:flex-row sm:items-start">
        <div className="rounded-lg bg-amber-500/15 p-2">
          <Wrench className="h-5 w-5 text-amber-500" />
        </div>
        <div className="flex-1 space-y-2">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-medium">Maintenance in progress</p>
            <Badge variant="warning">Checks skipped</Badge>
            {pausedCount > 0 && (
              <Badge variant="warning" className="gap-1">
                <BellOff className="h-3 w-3" />
                Alerts paused ({pausedCount})
              </Badge>
            )}
          </div>
          <ul className="text-sm text-muted-foreground space-y-1">
            {orgWide.map((w) => (
              <li key={w.id}>
                <strong className="text-foreground">{w.title}</strong> — all monitors
                {w.pauseAlerts ? ' · alerts paused' : ' · alerts active'}
                {' · ends '}
                {new Date(w.endTime).toLocaleString()}
              </li>
            ))}
            {scoped.map((w) => (
              <li key={w.id}>
                <strong className="text-foreground">{w.title}</strong> — {w.monitor?.name}
                {w.pauseAlerts ? ' · alerts paused' : ' · alerts active'}
                {' · ends '}
                {new Date(w.endTime).toLocaleString()}
              </li>
            ))}
          </ul>
        </div>
        <Link href="/maintenance">
          <Button variant="outline" size="sm">
            Manage
          </Button>
        </Link>
      </CardContent>
    </Card>
  );
}
