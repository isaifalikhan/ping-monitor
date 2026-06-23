'use client';

import { Badge } from '@/components/ui/badge';
import { BellOff } from 'lucide-react';
import type { Monitor } from '@/lib/types';
import type { MaintenanceWindow } from '@/lib/types';
import { getMonitorMaintenanceState } from '@/lib/maintenance/utils';

export function MonitorMaintenanceBadge({
  monitor,
  windows = [],
}: {
  monitor: Pick<Monitor, 'id' | 'status' | 'maintenanceMode'>;
  windows?: MaintenanceWindow[];
}) {
  const state = getMonitorMaintenanceState(monitor, windows);
  if (!state.inMaintenance) return null;

  return (
    <div className="flex flex-wrap items-center gap-1">
      <Badge variant="warning" className="text-[10px] px-1.5 py-0">
        Maintenance
      </Badge>
      {state.alertsPaused && (
        <Badge variant="muted" className="text-[10px] px-1.5 py-0 gap-0.5">
          <BellOff className="h-2.5 w-2.5" />
          Alerts off
        </Badge>
      )}
    </div>
  );
}
